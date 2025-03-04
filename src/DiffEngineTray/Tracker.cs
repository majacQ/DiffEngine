using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

class Tracker :
    IAsyncDisposable
{
    Action active;
    Action inactive;
    ConcurrentDictionary<string, TrackedMove> moves = new(StringComparer.OrdinalIgnoreCase);
    ConcurrentDictionary<string, TrackedDelete> deletes = new(StringComparer.OrdinalIgnoreCase);
    AsyncTimer timer;
    int lastScanCount;

    public Tracker(Action active, Action inactive)
    {
        this.active = active;
        this.inactive = inactive;
        timer = new(
            ScanFiles,
            TimeSpan.FromSeconds(2),
            exception =>
            {
                ExceptionHandler.Handle("Failed to scan files", exception);
            });
    }

    Task ScanFiles(DateTime dateTime, CancellationToken cancellationToken)
    {
        foreach (var delete in deletes.ToList()
            .Where(delete => !File.Exists(delete.Value.File)))
        {
            deletes.TryRemove(delete.Key, out _);
        }

        var newCount = moves.Count + deletes.Count;
        if (lastScanCount != newCount)
        {
            ToggleActive();
        }

        lastScanCount = newCount;
        return Task.WhenAll(moves.Select(HandleScanMove));
    }

    async Task HandleScanMove(KeyValuePair<string, TrackedMove> pair)
    {
        void RemoveAndKill(KeyValuePair<string, TrackedMove> keyValuePair)
        {
            if (moves.TryRemove(keyValuePair.Key, out var removed))
            {
                KillProcesses(removed);
            }
        }

        var move = pair.Value;
        if (!File.Exists(move.Temp))
        {
            RemoveAndKill(pair);
            return;
        }

        if (!File.Exists(move.Target))
        {
            return;
        }

        if (await FileComparer.FilesAreEqual(move.Temp, move.Target))
        {
            RemoveAndKill(pair);
            return;
        }
    }

    void ToggleActive()
    {
        if (TrackingAny)
        {
            active();
        }
        else
        {
            inactive();
        }
    }

    public bool TrackingAny
    {
        get => moves.Any() ||
               deletes.Any();
    }

    public TrackedMove AddMove(
        string temp,
        string target,
        string? exe,
        string? arguments,
        bool canKill,
        int? processId)
    {
        var exeFile = Path.GetFileName(exe);
        var targetFile = Path.GetFileName(target);
        return moves.AddOrUpdate(
            target,
            addValueFactory: key =>
            {
                Process? process = null;
                if (processId != null)
                {
                    ProcessEx.TryGet(processId.Value, out process);
                }

                var solutionName = SolutionDirectoryFinder.Find(key);

                Log.Information("MoveAdded. Target:{target}, CanKill:{canKill}, ProcessId:{processId}, CommandLine:{commandLine}", targetFile, canKill, processId, $"{exeFile} {arguments}");
                return new(temp, key, exe, arguments, canKill, process, solutionName);
            },
            updateValueFactory: (key, existing) =>
            {
                Process? process;
                if (processId == null)
                {
                    process = existing.Process;
                }
                else
                {
                    existing.Process?.Dispose();
                    ProcessEx.TryGet(processId.Value, out process);
                }

                var solutionName = SolutionDirectoryFinder.Find(key);
                Log.Information("MoveUpdated. Target:{target}, CanKill:{canKill}, ProcessId:{processId}, CommandLine:{commandLine}", targetFile, canKill, processId, $"{exeFile} {arguments}");
                return new(temp, key, exe, arguments, canKill, process, solutionName);
            });
    }

    public TrackedDelete AddDelete(string file)
    {
        return deletes.AddOrUpdate(
            file,
            addValueFactory: key =>
            {
                var solutionName = SolutionDirectoryFinder.Find(key);
                Log.Information("DeleteAdded. File:{file}", file, solutionName);
                return new(key, solutionName);
            },
            updateValueFactory: (_, existing) =>
            {
                Log.Information("DeleteUpdated. File:{file}", file);
                return existing;
            });
    }

    public void Accept(TrackedDelete delete)
    {
        if (deletes.Remove(delete.File, out var removed))
        {
            File.Delete(removed.File);
        }
    }

    public void Accept(IEnumerable<TrackedDelete> toAccept)
    {
        foreach (var delete in toAccept)
        {
            if (deletes.Remove(delete.File, out var removed))
            {
                File.Delete(removed.File);
            }
        }
    }

    public void Accept(IEnumerable<TrackedMove> toAccept)
    {
        foreach (var move in toAccept)
        {
            if (moves.Remove(move.Target, out var removed))
            {
                InnerMove(removed);
            }
        }
    }

    public void Accept(TrackedMove move)
    {
        if (moves.Remove(move.Target, out var removed))
        {
            InnerMove(removed);
        }
    }

    static void InnerMove(TrackedMove move)
    {
        if (File.Exists(move.Temp))
        {
            try
            {
                File.Move(move.Temp, move.Target, true);
            }
            catch (IOException exception)
            {
                Log.Error(exception, $"Filed to move '{move.Temp}' to '{move.Target}'.");
                //Swallow this since it is likely that a running test it reading or
                //writing to the files, and the result will re-add thetracked item
            }
        }

        KillProcesses(move);
    }

    static void KillProcesses(TrackedMove move)
    {
        if (!move.CanKill)
        {
            Log.Information($"Did not kill for `{move.Name}` since CanKill=false");
            return;
        }

        if (move.Process == null)
        {
            Log.Information($"No processes to kill for `{move.Name}`");
            return;
        }

        KillProcess(move, move.Process);
    }

    static void KillProcess(TrackedMove move, Process process)
    {
        try
        {
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            // Race condition can cause "No process is associated with this object"
        }
        catch (Exception exception)
        {
            ExceptionHandler.Handle($"Failed to kill process. Command: {move.Exe} {move.Arguments}", exception);
        }
        finally
        {
            process.Dispose();
        }
    }

    public void Clear()
    {
        deletes.Clear();

        foreach (var move in moves.Values)
        {
            KillProcesses(move);
        }

        moves.Clear();
    }

    public void AcceptOpen()
    {
        AcceptAllDeletes();

        foreach (var (key, move) in moves)
        {
            if (move.Process == null)
            {
                continue;
            }

            if (move.Process.HasExited)
            {
                continue;
            }

            InnerMove(move);
            moves.Remove(key, out _);
        }
    }

    public void AcceptAll()
    {
        AcceptAllDeletes();

        AcceptAllMoves();
    }

    public void AcceptAllDeletes()
    {
        foreach (var delete in deletes.Values)
        {
            File.Delete(delete.File);
        }

        deletes.Clear();
    }

    public void AcceptAllMoves()
    {
        foreach (var move in moves.Values)
        {
            InnerMove(move);
        }

        moves.Clear();
    }

    public ICollection<TrackedDelete> Deletes
    {
        get => deletes.Values;
    }

    public ICollection<TrackedMove> Moves
    {
        get => moves.Values;
    }

    public ValueTask DisposeAsync()
    {
        Clear();
        return timer.DisposeAsync();
    }
}