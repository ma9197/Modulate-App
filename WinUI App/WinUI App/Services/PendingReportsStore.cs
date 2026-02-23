using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using WinUI_App.Models;

namespace WinUI_App.Services
{
    public class PendingReportsStore
    {
        private readonly string _reportsRoot;
        private readonly string _indexPath;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public PendingReportsStore(string reportsRoot)
        {
            _reportsRoot = reportsRoot;
            _indexPath = Path.Combine(_reportsRoot, "pending_reports.json");
        }

        public string ReportsRoot => _reportsRoot;

        public List<PendingReportItem> Load()
        {
            try
            {
                if (!File.Exists(_indexPath))
                {
                    return new List<PendingReportItem>();
                }

                var json = File.ReadAllText(_indexPath);
                return JsonSerializer.Deserialize<List<PendingReportItem>>(json) ?? new List<PendingReportItem>();
            }
            catch
            {
                return new List<PendingReportItem>();
            }
        }

        public void Save(IEnumerable<PendingReportItem> items)
        {
            Directory.CreateDirectory(_reportsRoot);
            var json = JsonSerializer.Serialize(items, _jsonOptions);
            File.WriteAllText(_indexPath, json);
        }

        public string GetPendingFolder(string pendingId)
        {
            return Path.Combine(_reportsRoot, "Pending", pendingId);
        }

        public string ToRelativePath(string fullPath)
        {
            return Path.GetRelativePath(_reportsRoot, fullPath);
        }

        public string ToAbsolutePath(string relativePath)
        {
            return Path.Combine(_reportsRoot, relativePath);
        }

        public void RemoveFolder(string pendingId)
        {
            try
            {
                var folder = GetPendingFolder(pendingId);
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch
            {
            }
        }

        public void SaveItem(List<PendingReportItem> items, PendingReportItem item)
        {
            var existing = items.FirstOrDefault(x => x.Id == item.Id);
            if (existing != null)
            {
                items.Remove(existing);
            }
            items.Add(item);
            Save(items);
        }
    }
}

