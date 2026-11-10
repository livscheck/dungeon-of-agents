using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

public enum PrepState { Running, Done, Failed }

public sealed class PrepInfo
{
    public PrepState State = PrepState.Running;
    public DateTime Started = DateTime.Now, Finished;
    public string Message = "";
}

// Prepares the local test environment for a project that is ready to test. It does one thing: runs the plugin's
// my-plugin:test-prep skill in the background (hidden, no terminal), inside the project's repo.
public static class TestPrep
{
    static readonly Dictionary<string, PrepInfo> Runs = new();
    static readonly ConcurrentQueue<string> Finished = new();
    static readonly ConcurrentDictionary<int, byte> PrepPids = new();

    /// <summary>
    /// True when <paramref name="pid"/> is a hidden test-prep run this game started. Those runs show up among the
    /// running Claude sessions, in the project's repo, but nobody can talk to them.
    /// </summary>
    public static bool IsPrepProcess(int pid) => PrepPids.ContainsKey(pid);

    public static PrepInfo? Get(string number) { lock (Runs) return Runs.TryGetValue(number, out var p) ? p : null; }
    public static void Reset(string number) { lock (Runs) Runs.Remove(number); }
    public static void Begin(string number) { lock (Runs) Runs[number] = new PrepInfo(); }

    /// <summary>The projects whose prep has finished since the last call.</summary>
    public static List<string> Poll()
    {
        var list = new List<string>();
        while (Finished.TryDequeue(out var n)) list.Add(n);
        return list;
    }

    static void Finish(string number, PrepState state, string message)
    {
        lock (Runs)
        {
            if (Runs.TryGetValue(number, out var i)) { i.State = state; i.Message = message; i.Finished = DateTime.Now; }
        }
        Finished.Enqueue(number);
    }

    /// <summary>Start the skill in the background, with the project's repo as the working folder. Returns at once.</summary>
    public static string Launch(string number, string repoDir)
    {
        if (AgentLauncher.DryRun)
        {
            // Dry run: nothing is started or copied; pretend it finished after a moment.
            Task.Delay(2500).ContinueWith(_ => Finish(number, PrepState.Done, "Dry run: nothing was copied."));
            return $"DRYRUN claude -p /{Settings.TestPrepSkill} {number}  (in {repoDir})";
        }

        string prompt =
            $"/{Settings.TestPrepSkill} {number}\n\n" +
            "This is an unattended background run. The current folder is the project's clone, so use it as the source " +
            "(no need to search, or to ask how the skill is being invoked). Do not ask questions. Finish with the usual short report.";
        try
        {
            var psi = new ProcessStartInfo("claude")
            {
                WorkingDirectory = Directory.Exists(repoDir) ? repoDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-p", prompt, "--output-format", "json", "--no-session-persistence",
                                      "--permission-mode", "acceptEdits", "--allowedTools", "Skill", "Bash", "Read", "Glob", "Grep" })
                psi.ArgumentList.Add(a);

            var p = Process.Start(psi)!;
            int pid = p.Id;
            PrepPids[pid] = 0;
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => PrepPids.TryRemove(pid, out _);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            Task.Run(() =>
            {
                if (!p.WaitForExit(10 * 60 * 1000))
                {
                    try { p.Kill(true); } catch { }
                    Finish(number, PrepState.Failed, "Timed out after 10 minutes.");
                    return;
                }
                try
                {
                    using var doc = JsonDocument.Parse(stdout.Result);
                    var r = doc.RootElement;
                    string result = r.TryGetProperty("result", out var rv) && rv.ValueKind == JsonValueKind.String ? rv.GetString() ?? "" : "";
                    bool isError = r.TryGetProperty("is_error", out var ev) && ev.ValueKind == JsonValueKind.True;
                    int denials = r.TryGetProperty("permission_denials", out var dv) && dv.ValueKind == JsonValueKind.Array ? dv.GetArrayLength() : 0;
                    if (isError || p.ExitCode != 0)
                        Finish(number, PrepState.Failed, Ui.Excerpt(result != "" ? result : stderr.Result, 220));
                    else if (denials > 0)
                        Finish(number, PrepState.Failed, $"Claude was blocked from {denials} action(s). Open the agent in a terminal and run /{Settings.TestPrepSkill} there.");
                    else
                        Finish(number, PrepState.Done, Ui.Excerpt(result, 220));
                }
                catch (Exception ex)
                {
                    Finish(number, PrepState.Failed, $"Could not read the result: {ex.Message}");
                }
            });
            return $"Test prep for {number} is running in the background.";
        }
        catch (Exception ex)
        {
            Finish(number, PrepState.Failed, $"Could not start: {ex.Message}");
            return $"Could not start test prep: {ex.Message}";
        }
    }
}
