using SnapStak.Wasm.Client.Models.Pillars;
using SnapStak.Wasm.Client.Storage;

namespace SnapStak.Wasm.Client.Services;

/// <summary>
/// Writes and reads the Influence pillar (Pillar 3).
/// Port of InfluenceAgentCom — storage only, no AI.
/// </summary>
public sealed class InfluenceService
{
    private readonly IPillarStorage _storage;
    public InfluenceService(IPillarStorage storage) => _storage = storage;

    public void WriteInfluence(string userUuid, string componentId, InfluenceData influence)
    {
        var dir = _storage.ResolveComponentDir(userUuid, componentId);
        _storage.WriteInfluence(dir, componentId, influence);
    }

    public InfluenceData? ReadInfluence(string userUuid, string componentId)
    {
        var dir = _storage.ResolveComponentDir(userUuid, componentId);
        return _storage.ReadInfluence(dir, componentId);
    }
}

/// <summary>
/// Writes and reads the Objective pillar (Pillar 4).
/// Port of ObjectiveAgentCom — storage only, no AI.
/// </summary>
public sealed class ObjectiveService
{
    private readonly IPillarStorage _storage;
    public ObjectiveService(IPillarStorage storage) => _storage = storage;

    public void WriteObjective(string userUuid, string componentId, ObjectiveData objective)
    {
        var dir = _storage.ResolveComponentDir(userUuid, componentId);
        _storage.WriteObjective(dir, componentId, objective);
    }

    public void UpdateObjective(string userUuid, string componentId, string framework,
        int? screenWidthTarget, string? deviceType, string? modeInstruction, int[]? breakpoints)
    {
        var dir = _storage.ResolveComponentDir(userUuid, componentId);
        var existing = _storage.ReadObjective(dir, componentId) ?? new ObjectiveData { ComponentId = componentId };
        existing.Framework         = framework;
        existing.ScreenWidthTarget = screenWidthTarget;
        existing.DeviceType        = deviceType;
        existing.AdditionalIntent  = modeInstruction;
        if (breakpoints?.Length > 0) existing.CapturedBreakpoints = breakpoints;
        _storage.WriteObjective(dir, componentId, existing);
    }
}
