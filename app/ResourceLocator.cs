using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace IsaacDiorama;

/// <summary>
/// Finds the Isaac install so a first-time user does not have to hand-write a path.
///
/// Deliberately REGISTRY-FREE. Steam already publishes every library folder in
/// `steamapps/libraryfolders.vdf`, which is plain text — reading that needs no extra package,
/// no Windows-only API surface beyond the file system, and keeps working when Steam is
/// installed somewhere unusual. The registry would only tell us where Steam itself lives,
/// which is the easy half of the problem; the game is usually on another drive.
///
/// Resolution order in Program: command-line argument, then `resources_root.txt`, then this.
/// An explicit path always wins, so auto-detection can never override a deliberate choice.
/// </summary>
internal static class ResourceLocator
{
    public const string GameFolderName = "The Binding of Isaac Rebirth";

    /// <summary>
    /// The game directory, or null. `how` describes where it came from (or where we looked),
    /// so a failed start can say something more useful than "not found".
    /// </summary>
    public static string? FindGameDir(out string how)
    {
        var tried = new List<string>();
        try
        {
            var libraries = new List<string>();
            void AddLib(string? p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                p = p!.Trim();
                if (Directory.Exists(p) && !libraries.Contains(p)) libraries.Add(p);
            }

            // Well-known Steam roots, then every fixed drive's usual spellings. A library
            // does not have to be a Steam INSTALL, so both are probed.
            foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles" })
            {
                string? pf = Environment.GetEnvironmentVariable(env);
                if (!string.IsNullOrEmpty(pf)) AddLib(Path.Combine(pf!, "Steam"));
            }
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                string r = drive.RootDirectory.FullName;
                AddLib(Path.Combine(r, "Steam"));
                AddLib(Path.Combine(r, "SteamLibrary"));
                AddLib(Path.Combine(r, "Games", "Steam"));
                AddLib(Path.Combine(r, "Program Files (x86)", "Steam"));
            }

            // Steam's own index of every library, including ones we would never guess.
            // The file is a VDF; we only need the quoted "path" values out of it.
            for (int i = 0; i < libraries.Count; i++)   // grows as we parse; intentional
            {
                string vdf = Path.Combine(libraries[i], "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                try
                {
                    string text = File.ReadAllText(vdf);
                    foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\"",
                                                      RegexOptions.IgnoreCase))
                        AddLib(m.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch { /* unreadable library index: just skip it */ }
            }

            // Prefer an install that ALREADY has extracted resources: a second copy without
            // them would "work" and then render nothing, which is the confusing outcome.
            string? fallback = null;
            foreach (var lib in libraries)
            {
                string game = Path.Combine(lib, "steamapps", "common", GameFolderName);
                if (!Directory.Exists(game)) continue;
                tried.Add(game);
                if (Directory.Exists(Path.Combine(game, "extracted_resources", "resources", "gfx")))
                { how = $"auto-detected (with extracted resources): {game}"; return game; }
                fallback ??= game;
            }
            if (fallback != null)
            {
                how = $"auto-detected, but NO extracted_resources/resources/gfx in: {fallback}";
                return fallback;
            }
        }
        catch (Exception ex)
        {
            how = $"auto-detect failed: {ex.Message}";
            return null;
        }

        how = tried.Count > 0
            ? "auto-detect found the game but no resources: " + string.Join(" | ", tried)
            : "auto-detect found no Steam copy of \"" + GameFolderName + "\"";
        return null;
    }
}
