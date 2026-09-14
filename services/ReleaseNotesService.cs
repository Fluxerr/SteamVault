using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using SteamVault.Models;

namespace SteamVault.Services;

public class ReleaseNotesService
{
    public static ObservableCollection<WhatsNewItem> GetReleaseItems(string version, string? rawReleaseBody = null)
    {
        var items = new ObservableCollection<WhatsNewItem>();

        // Try parsing raw GitHub release notes body if available
        if (!string.IsNullOrWhiteSpace(rawReleaseBody) && rawReleaseBody != "A new version of SteamVault was installed.")
        {
            var parsed = ParseGitHubMarkdown(rawReleaseBody);
            foreach (var item in parsed)
            {
                items.Add(item);
            }
        }

        // If no parsed items (or running locally without GitHub release body), use curated release notes for current version
        if (items.Count == 0)
        {
            var defaultItems = GetCuratedReleaseNotes(version);
            foreach (var item in defaultItems)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static List<WhatsNewItem> ParseGitHubMarkdown(string markdown)
    {
        var items = new List<WhatsNewItem>();
        var lines = markdown.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        string currentCategory = "IMPROVED";

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // Detect section headers e.g. "## Features", "### Bug Fixes"
            if (line.StartsWith("#"))
            {
                var headerText = line.TrimStart('#').Trim().ToLowerInvariant();
                if (headerText.Contains("new") || headerText.Contains("feature"))
                    currentCategory = "NEW";
                else if (headerText.Contains("performance") || headerText.Contains("speed"))
                    currentCategory = "PERFORMANCE";
                else if (headerText.Contains("fix"))
                    currentCategory = "FIXED";
                else if (headerText.Contains("theme") || headerText.Contains("ui"))
                    currentCategory = "THEME";
                else
                    currentCategory = "IMPROVED";

                continue;
            }

            // Detect bullet items e.g. "- Added feature", "* Fixed issue"
            if (line.StartsWith("-") || line.StartsWith("*") || line.StartsWith("•"))
            {
                var text = line.Substring(1).Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                // Split title and description if separated by ":" or " - "
                string title = text;
                string desc = string.Empty;

                int colonIdx = text.IndexOf(':');
                int dashIdx = text.IndexOf(" - ");

                if (colonIdx > 0 && (dashIdx < 0 || colonIdx < dashIdx))
                {
                    title = text.Substring(0, colonIdx).Trim();
                    desc = text.Substring(colonIdx + 1).Trim();
                }
                else if (dashIdx > 0)
                {
                    title = text.Substring(0, dashIdx).Trim();
                    desc = text.Substring(dashIdx + 3).Trim();
                }

                // Determine tag based on title keywords if not already set by header
                string tag = currentCategory;
                var lowerTitle = title.ToLowerInvariant();
                if (lowerTitle.Contains("fix")) tag = "FIXED";
                else if (lowerTitle.Contains("perf") || lowerTitle.Contains("fast") || lowerTitle.Contains("speed")) tag = "PERFORMANCE";
                else if (lowerTitle.Contains("add") || lowerTitle.Contains("new")) tag = "NEW";
                else if (lowerTitle.Contains("theme") || lowerTitle.Contains("style")) tag = "THEME";

                items.Add(new WhatsNewItem
                {
                    Tag = tag,
                    Title = title,
                    Description = desc
                });
            }
        }

        return items;
    }

    private static List<WhatsNewItem> GetCuratedReleaseNotes(string version)
    {
        return new List<WhatsNewItem>
        {
            new WhatsNewItem
            {
                Tag = "NEW",
                Title = "Pure Local Lua Generation",
                Description = "Decoupled third-party manifest servers, removed login dialogs and daily quotas. SteamVault now generates game unlocks locally without external limits."
            },
            new WhatsNewItem
            {
                Tag = "PERFORMANCE",
                Title = "Sub-Second Batch Updater Engine",
                Description = "Powered by native Windows tar and robocopy utilities. Updates and file swaps now complete instantly in under 0.5 seconds."
            },
            new WhatsNewItem
            {
                Tag = "IMPROVED",
                Title = "Smooth Progress & Thread Safety",
                Description = "Optimized progress streaming during downloads and imports. The main application stays fast, fluid, and responsive at all times."
            },
            new WhatsNewItem
            {
                Tag = "THEME",
                Title = "Unified Gruvbox & Custom Themes",
                Description = "Seamless theme integration across all windows, including the updater and What's New dialogs, matching your custom color settings."
            },
            new WhatsNewItem
            {
                Tag = "FIXED",
                Title = "Process & Relaunch Stability",
                Description = "Resolved post-update batch script execution errors, instant temp file cleanup, and smooth application handoff."
            }
        };
    }
}
