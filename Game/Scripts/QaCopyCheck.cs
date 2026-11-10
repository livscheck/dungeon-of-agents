using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

// After an agent merges into qa and updates the QA sync list, the user copies the changed files from staging to the
// QA folder by hand. This checks that copy, read-only, so nobody has to say it is done:
//   - the files: the project's block in the QA sync lists ("## PRJ-<number> ..." then one path per line, relative
//     to the app root), in each file listed in qaSyncLists
//   - a file counts as copied when the QA folder's copy is byte-for-byte the same as the project repo's copy
// It never writes anything under G:\.
public static class QaCopyCheck
{
    public static string QaRoot => Settings.QaRoot;
    static IReadOnlyList<string> SyncLists => Settings.QaSyncLists;

    public sealed record Result(int Copied, int Total, List<string> Missing)
    {
        public bool Done => Total > 0 && Copied == Total;
    }

    // Hashes keyed by path + size + time written, so unchanged files on G:\ are not read again every check.
    static readonly Dictionary<string, string> HashCache = new(StringComparer.OrdinalIgnoreCase);

    static string Hash(string path)
    {
        var fi = new FileInfo(path);
        string key = $"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        lock (HashCache) if (HashCache.TryGetValue(key, out var h)) return h;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var hash = Convert.ToHexString(SHA256.HashData(fs));
        lock (HashCache) HashCache[key] = hash;
        return hash;
    }

    /// <summary>The app-relative paths listed for the project in the QA sync lists (empty until the agent adds its block).</summary>
    public static List<string> ListedFiles(string number)
    {
        var files = new List<string>();
        foreach (var rel in SyncLists)
        {
            var path = Path.Combine(QaRoot, rel);
            if (!File.Exists(path)) continue;
            bool inBlock = false;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith("##")) { inBlock = line.Contains(number, StringComparison.OrdinalIgnoreCase); continue; }
                if (inBlock && line != "") files.Add(line.Replace('/', '\\'));
            }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>How many of the project's QA-listed files are in the QA folder and match the repo's copy.</summary>
    public static Result Check(string number, string repoDir)
    {
        var listed = ListedFiles(number);
        var missing = new List<string>();
        int copied = 0;
        foreach (var rel in listed)
        {
            string qa = Path.Combine(QaRoot, rel), repo = Path.Combine(repoDir, rel);
            try
            {
                if (!File.Exists(repo)) { copied++; continue; } // not in the repo (e.g. a deleted file): nothing to match
                if (File.Exists(qa) && new FileInfo(qa).Length == new FileInfo(repo).Length && Hash(qa) == Hash(repo)) copied++;
                else missing.Add(rel);
            }
            catch { missing.Add(rel); }
        }
        return new Result(copied, listed.Count, missing);
    }

    /// <summary>True when a dashboard status means the agent is waiting for the user's copy into QA.</summary>
    public static bool IsWaitingForCopy(string status)
    {
        // "Merged to QA" on its own counts too: an agent briefed before this check existed sets just that, and it is
        // the same moment (merged and listed, files not yet confirmed in the QA folder). "In QA" ends it.
        var s = status.Trim().ToLowerInvariant();
        return s.Contains("waiting for files") || s.Contains("copy files") || s.Contains("files to be copied") || s.StartsWith("merged to qa");
    }
}
