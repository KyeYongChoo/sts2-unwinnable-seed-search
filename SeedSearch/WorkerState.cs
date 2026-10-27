using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Saves;

/// <summary>A search worker's saved progress: enough to resume its seed sequence exactly, plus stats for the UI.</summary>
class WorkerState
{
    public ulong From { get; set; }
    public long Checked { get; set; }
    public long Target { get; set; }
    public int Counter { get; set; }
    public ulong S0 { get; set; }
    public ulong S1 { get; set; }
    public ulong S2 { get; set; }
    public ulong S3 { get; set; }
    public Dictionary<string, long> Funnel { get; set; } = new();
    public double Rate { get; set; }
    public DateTime Updated { get; set; }
    public int Pid { get; set; }
    public bool Finished { get; set; }
    public string ConfigHash { get; set; } = "";

    public static WorkerState Capture(ulong from, long done, long target, Rng rng, IDictionary<string, long> funnel, double rate, bool finished, string configHash)
    {
        var s = rng.ToSerializable();
        return new WorkerState
        {
            From = from, Checked = done, Target = target, Counter = s.counter,
            S0 = s.state0, S1 = s.state1, S2 = s.state2, S3 = s.state3,
            Funnel = new(funnel), Rate = rate, Updated = DateTime.UtcNow,
            Pid = Environment.ProcessId, Finished = finished, ConfigHash = configHash,
        };
    }

    public SerializableRng ToRng() => new() { counter = Counter, state0 = S0, state1 = S1, state2 = S2, state3 = S3 };
}
