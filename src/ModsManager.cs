using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO.Compression;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Interactivity;

namespace QuestPatcher {
    public class ModsManager
    {
        // Temporarily extract the ZIP file here so that we can use ADB push
        private const string TEMP_EXTRACT_PATH = "./extractedMod/";
        private string INSTALLED_MODS_PATH = "sdcard/QuestPatcher/{app-id}/installedMods/";

        private MainWindow window;
        private DebugBridge debugBridge;

        // Map of Mod ID -> Mod Manifest
        public Dictionary<string, ModManifest> InstalledMods { get; } = new Dictionary<string, ModManifest>();

        public ModsManager(MainWindow window) {
            this.window = window;
            this.debugBridge = window.DebugBridge;
        }

        public async Task LoadModsFromQuest() {

            await debugBridge.runCommandAsync("shell mkdir -p " + INSTALLED_MODS_PATH);
            await debugBridge.runCommandAsync("shell mkdir -p sdcard/Android/data/{app-id}/files/mods");
            await debugBridge.runCommandAsync("shell mkdir -p sdcard/Android/data/{app-id}/files/libs");

            // List the manifests in the installed mods directory for this app
            string modsNonSplit = await debugBridge.runCommandAsync("shell ls -R " + INSTALLED_MODS_PATH);

            // Remove unnecessary things that ADB adds
            string[] rawPaths = modsNonSplit.Split("\n");
            List<string> parsedPaths = new List<string>();
            for(int i = 0; i < rawPaths.Length; i++) {
                if(i == 0 || i == rawPaths.Length - 1) {continue;}

                parsedPaths.Add(INSTALLED_MODS_PATH + rawPaths[i].Replace("\r", ""));
            }

            foreach(string path in parsedPaths) {
                string contents = await debugBridge.runCommandAsync("shell cat " + path);

                ModManifest manifest = ModManifest.Load(contents);

                addManifest(manifest);
            }
        }

        public async Task InstallMod(string path) {
            // Extract the mod archive temporarily so that we can push the files using adb
            if(Directory.Exists(TEMP_EXTRACT_PATH))
            {
                Directory.Delete(TEMP_EXTRACT_PATH, true);
            }

            window.log("Extracting mod . . .");
            Directory.CreateDirectory(TEMP_EXTRACT_PATH);
            ZipFile.ExtractToDirectory(path, TEMP_EXTRACT_PATH);

            // Read the manifest
            window.log("Loading manifest . . .");
            string manifestText = await File.ReadAllTextAsync(TEMP_EXTRACT_PATH + "mod.json");
            ModManifest manifest = ModManifest.Load(manifestText);

            if(manifest.GameId != debugBridge.APP_ID) {
                throw new Exception("This mod is not indended for the selected game!");
            }

            if(InstalledMods.ContainsKey(manifest.Id))
            {
                throw new Exception("Attempted to install a mod when it was already installed");
            }

            // Copy all of the SO files
            foreach(string libraryPath in manifest.LibraryFiles) {
                window.log("Copying mod file " + libraryPath);
                string result = await debugBridge.runCommandAsync("push " + TEMP_EXTRACT_PATH + libraryPath + " sdcard/Android/data/{app-id}/files/libs/" + libraryPath);
            }

            foreach(string modFilePath in manifest.ModFiles) {
                window.log("Copying library file " + modFilePath);
                string result = await debugBridge.runCommandAsync("push " + TEMP_EXTRACT_PATH + modFilePath + " sdcard/Android/data/{app-id}/files/mods/" + modFilePath);
            }

            // Store that the mod was successfully installed
            window.log("Copying manifest . . .");
            debugBridge.runCommand("push " + TEMP_EXTRACT_PATH + "mod.json " + INSTALLED_MODS_PATH + manifest.Id + ".json");

            Directory.Delete(TEMP_EXTRACT_PATH, true);

            addManifest(manifest);
            window.log("Done!");
        }

        private async Task uninstallMod(ModManifest manifest)
        {
            window.log("Uninstalling mod with ID " + manifest.Id + " . . .");
            InstalledMods.Remove(manifest.Id);

            foreach (string modFilePath in manifest.ModFiles) {
                window.log("Removing mod file " + modFilePath);
                await debugBridge.runCommandAsync("shell rm -f sdcard/Android/data/{app-id}/files/mods/" + modFilePath); // Remove each mod file
            }

            
            foreach (string libraryPath in manifest.LibraryFiles)
            {
                // Only remove libraries if they aren't used by another mod
                bool isUsedElsewhere = false;
                foreach(ModManifest otherManifest in InstalledMods.Values)
                {
                    if(otherManifest.LibraryFiles.Contains(libraryPath))
                    {
                        window.log("Other mod " + otherManifest.Id + " still needs library " + libraryPath + ", not removing");
                        isUsedElsewhere = true;
                        break;
                    }
                }

                if(!isUsedElsewhere)
                {
                    window.log("Removing library file " + libraryPath);
                    await debugBridge.runCommandAsync("shell rm -f sdcard/Android/data/{app-id}/files/libs/" + libraryPath);
                }
            }

            window.log("Removing mod manifest . . .");
            await debugBridge.runCommandAsync("shell rm -f " + INSTALLED_MODS_PATH + manifest.Id + ".json"); // Remove the mod manifest

            window.log("Done!");
        }

        // Kind of janky, but there isn't another good way to do this unless I set up MVVM which will take ages
        private void addManifest(ModManifest manifest) {
            Border border = new Border();
            border.Padding = new Thickness(5);
            border.BorderThickness = new Thickness(1);
            border.BorderBrush = Brushes.Gray;
            border.CornerRadius = new CornerRadius(3);

            StackPanel stackPanel = new StackPanel();
            border.Child = stackPanel;

            stackPanel.Orientation = Avalonia.Layout.Orientation.Vertical;
            
            TextBlock name = new TextBlock();
            name.Text = "Mod Name: " + manifest.Name;
            
            TextBlock version = new TextBlock();
            version.Text = "Mod Version: " + manifest.Version;

            TextBlock gameVersion = new TextBlock();
            gameVersion.Text = "Intended for game version: " + manifest.GameVersion;

            Button uninstall = new Button();
            uninstall.Content = "Uninstall Mod";
            uninstall.Click += async delegate (object? sender, RoutedEventArgs args)
            {
                try
                {
                    // Uninstall the mod from the Quest
                    await uninstallMod(manifest);
                    window.InstalledModsPanel.Children.Remove(border);

                    window.ModInstallErrorText.IsVisible = false;
                }
                catch (Exception ex)
                {
                    window.ModInstallErrorText.IsVisible = true;
                    window.ModInstallErrorText.Text = "Error while uninstalling mod: " + ex.Message;
                }
            };

            stackPanel.Children.Add(name);
            stackPanel.Children.Add(version);
            stackPanel.Children.Add(gameVersion);
            stackPanel.Children.Add(uninstall);

            window.InstalledModsPanel.Children.Add(border);
            InstalledMods[manifest.Id] = manifest;
        }

    }
}