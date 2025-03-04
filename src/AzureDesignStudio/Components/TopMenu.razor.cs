﻿using AntDesign;
using AzureDesignStudio.Core;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Core.Models;
using Microsoft.JSInterop;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;
using Blazor.Diagrams.Core.Models;
using AzureDesignStudio.Components.MenuDrawer;
using AzureDesignStudio.Models;
using Microsoft.AspNetCore.Components;
using AzureDesignStudio.Core.VirtualNetwork;

namespace AzureDesignStudio.Components
{
    public partial class TopMenu
    {
        private DrawerRef<string>? drawerRef;
        private string openedDrawer = string.Empty;
        private string imgUrl = null!;

        private async Task HandleMenuItemClicked(MenuItem menuItem)
        {
            switch (menuItem.Key)
            {
                case "export":
                    await OpenExportDrawer();
                    break;
                case "save":
                    await OpenSaveDrawer();
                    break;
                case "load":
                    await OpenLoadDrawer();
                    break;
                case "user":
                    await OpenUserDrawer();
                    break;
                default:
                    {
                        await ResetDrawerRef(string.Empty);
                        break;
                    }
            }
        }

        private Task CloseDrawer()
        {
            // Deselect the ant menu item. A bit strange way.
            // Tracked here: https://github.com/ant-design-blazor/ant-design-blazor/issues/2159
            topMenu.SelectItem(new MenuItem());
            drawerRef = null;
            openedDrawer = string.Empty;
            return Task.CompletedTask;
        }
        private async Task<DrawerRef<string>> OpenDrawer<TDrawerTemplate>(string title, string options, int width = 350) 
            where TDrawerTemplate : FeedbackComponent<string, string>
        {
            var drawerOptions = new DrawerOptions()
            {
                Title = title,
                Width = width,
            };

            var dr = await drawerService.CreateAsync<TDrawerTemplate, string, string>(drawerOptions, options);
            dr.OnClose = CloseDrawer;
            return dr;
        }
        private async Task<bool> ResetDrawerRef(string drawerName)
        {
            if (openedDrawer == drawerName)
                return false;
            openedDrawer = drawerName;
            if (drawerRef is not null)
            {
                await drawerRef.CloseAsync();
                drawerRef = null;
            }

            return true;
        }
        private async Task OpenUserDrawer()
        {
            if (!await ResetDrawerRef("user"))
                return;

            drawerRef = await OpenDrawer<UserDrawerTemplate>("User account", "user");
            drawerRef.OnClosed = async result =>
            {
                await CloseDrawer();
            };
        }
        private async Task OpenExportDrawer()
        {
            if (!await ResetDrawerRef("export"))
                return;

            drawerRef = await OpenDrawer<ExportDrawerTemplate>("Export the design", "export");
            drawerRef.OnClosed = async result =>
            {
                await CloseDrawer();

                if (adsContext.Diagram.Groups.Count == 0 && adsContext.Diagram.Nodes.Count == 0)
                {
                    await messageService.Warn("There is nothing to export.");
                    return;
                }

                switch (result)
                {
                    case "arm":
                        await ExportArmTemplate();
                        break;
                    case "img":
                        await InvokePrintJS();
                        break;
                };
            };
        }

        private async Task ExportArmTemplate()
        {
            var armTemplate = new ArmTemplate();
            try
            {
                foreach (var group in adsContext.Diagram.Groups.Where(g => g.Group == null || g is not SubnetModel))
                {
                    if (group is IAzureResource res)
                    {
                        armTemplate.AddParameters(res.GetArmParameters());
                        armTemplate.AddResource(res.GetArmResources());
                    }
                }
                foreach (var node in adsContext.Diagram.Nodes.Where(n => n is not GroupModel))
                {
                    if (node is IAzureResource res)
                    {
                        armTemplate.AddParameters(res.GetArmParameters());
                        armTemplate.AddResource(res.GetArmResources());
                    }
                }
            }
            catch (Exception ex)
            {
                await messageService.Error($"{ex.Message}");
                return;
            }

            // Somehow using SerializeAsync to a stream and use it for the download directly doesn't work...
            var armString = JsonSerializer.Serialize(armTemplate.Template,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    WriteIndented = true,
                }
            );

            await OpenJsonDrawer(armString);
        }
        private async Task OpenJsonDrawer(string json)
        {
            var currentWindowSize = await JS.InvokeAsync<WindowSize>("getWindowSize");
            var options = new DrawerOptions
            {
                Title = "ARM Template",
                Placement = "bottom",
                Height = currentWindowSize.Height
            };

            await drawerService.CreateAsync<CodeDrawerTemplate, string, string>(options, json);
        }

        private async Task InvokePrintJS()
        {
            var bound = await adsContext.Diagram.BeforePrint();
            
            try
            {
                imgUrl = await JS.InvokeAsync<string>("diagram2PicAsync", "png", bound!.Width, bound!.Height);
            }
            catch (JSException ex)
            {
                logger.LogError($"Export failed: {ex.Message}");
            }
            finally
            {
                await Task.Run(async () =>
                {
                    // Need to wait a while for BeforePrint to take effect. 
                    // 500ms might not be enough for large design. Need more tests.
                    await Task.Delay(500);
                    adsContext.Diagram.AfterPrint();
                });
            }

            if (!string.IsNullOrEmpty(imgUrl))
            {
                showImgPreview = true;
                StateHasChanged();
            }
        }

        private async Task OpenSaveDrawer()
        {
            if (!await ResetDrawerRef("save"))
                return;

            drawerRef = await OpenDrawer<SaveDrawerTemplate>("Save the design", "save");
            drawerRef.OnClosed = async result =>
            {
                await CloseDrawer();
                await SaveDiagram(result);
            };
        }
        private async Task SaveDiagram(string designName)
        {
            if (string.IsNullOrWhiteSpace(designName))
            {
                logger.LogInformation($"Design name is null or empty.");
                return;
            }

            var diagramGraph = await DataModelFactory.SaveDiagramToDto(adsContext.Diagram, mapper);
            if (diagramGraph == null)
            {
                await messageService.Warn("There is nothing to save.");
                return;
            }
            var graph = JsonSerializer.Serialize(diagramGraph);

            var statusCode = await designService.SaveDesign(designName, graph);
            if (statusCode >= 200 && statusCode <= 299)
            {
                await messageService.Success("The design is saved successfully.");
            }
            else
            {
                await messageService.Error($"Failed to save the design. Error code: {statusCode}");
            }
        }

        private async Task OpenLoadDrawer()
        {
            if (!await ResetDrawerRef("load"))
                return;

            drawerRef = await OpenDrawer<LoadDrawerTemplate>("Load your design", "load");
            drawerRef.OnClosed = async result =>
            {
                await CloseDrawer();
                await LoadDiagram(result);
            };
        }

        private async Task LoadDiagram(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                logger.LogInformation($"The file path is null or empty.");
                return;
            }

            // Clear the diagram before loading a new one.
            adsContext.Diagram.Nodes.Clear();
            adsContext.Diagram.Links.Clear();
            adsContext.Diagram.RemoveAllGroups();

            var loadingTask = messageService.Loading("Loading the design ...", 0);

            DiagramGraph? diagramGraph = null;
            var parts = filePath.Split("://");
            if (parts[0] == "usedb")
            {
                var (status, designData) = await designService.LoadDesign(parts[1]);
                if (status == 200 && !string.IsNullOrEmpty(designData))
                {
                    diagramGraph = JsonSerializer.Deserialize<DiagramGraph>(designData);
                }
            }
            else
            {
                var httpClient = clientFactory.CreateClient("AzureDesignStudio.ResourceAccess");

                diagramGraph = await httpClient.GetFromJsonAsync<DiagramGraph>(filePath);
            }

            if (diagramGraph == null)
            {
                await messageService.Error("Cannot load the graph from the uri.");
            }
            else
            {
                DataModelFactory.LoadDiagramFromDto(adsContext.Diagram, diagramGraph, mapper);
                if (parts.Length > 1)
                {
                    adsContext.CurrentDesignName = parts[1];
                }
            }

            loadingTask.Start();
        }
    }
}
