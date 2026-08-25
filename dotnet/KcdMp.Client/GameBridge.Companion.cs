using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace KcdMp.Client;

public partial class GameBridge
{
    private readonly CompanionProfileStore _companionProfileStore = new();
    private readonly CompanionSaveShield _companionSaveShield = new();
    private CompanionSaveShield.Shield? _activeSaveShield;
    private readonly CompanionSnapshotCollector _companionSnapshots = new();
    private readonly SemaphoreSlim _companionProfileGate = new(1, 1);

    private Func<byte, byte[], CancellationToken, Task>? _companionSendPacket;
    private CancellationTokenSource? _companionConnectionCts;
    private CancellationTokenSource? _companionCheckpointCts;
    private Task? _companionCheckpointTask;

    private CompanionInventorySnapshot? _singlePlayerInventoryBackup;
    private bool _companionInventoryActive;
    private bool _sessionEndedByHost;

    // takeId -> local transaction. The Lua side owns the WUID used for rollback;
    // this map is only for diagnostics and dedupe.
    private readonly Dictionary<uint, string> _companionOpenTakes = new();
    private readonly object _companionTakeLock = new();

    private bool HandleCompanionGameEvent(string name, string arg)
    {
        if (HandlePartyGameEvent(name, arg)) return true;
        switch (name)
        {
            case "comp_profile_begin":
            {
                // tag money health stamina
                var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4) return true;
                float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var money);
                float? hp = TryNullableFloat(p[2]);
                float? st = TryNullableFloat(p[3]);
                _companionSnapshots.Begin(p[0], money, hp, st);
                return true;
            }

            case "comp_profile_item":
            {
                // tag class amount health
                var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 4
                    || !Guid.TryParse(p[1], out var cls)
                    || !int.TryParse(p[2], out var amount)
                    || !float.TryParse(p[3], NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out var health))
                    return true;
                _companionSnapshots.Add(p[0], cls, amount, health);
                return true;
            }

            case "comp_profile_end":
                _companionSnapshots.End(arg.Trim());
                return true;

            case "loot_manifest_begin":
            case "loot_manifest_end":
            {
                var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2 || !uint.TryParse(p[0], out var snap)) return true;
                byte phase = name.EndsWith("begin", StringComparison.Ordinal)
                    ? Protocol.LootManifestPhaseBegin
                    : Protocol.LootManifestPhaseEnd;
                _ = SendLootManifestAsync(phase, snap, p[1], Guid.Empty, 0, 1f);
                return true;
            }

            case "loot_manifest_item":
            {
                // snapshot source class amount health
                var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 5
                    || !uint.TryParse(p[0], out var snap)
                    || !Guid.TryParse(p[2], out var cls)
                    || !ushort.TryParse(p[3], out var amount)
                    || !float.TryParse(p[4], NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out var health))
                    return true;
                _ = SendLootManifestAsync(
                    Protocol.LootManifestPhaseItem, snap, p[1], cls, amount, health);
                return true;
            }

            case "loot_take":
            {
                // source class amount health localWuid
                var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 5
                    || !Guid.TryParse(p[1], out var cls)
                    || !ushort.TryParse(p[2], out var amount)
                    || !float.TryParse(p[3], NumberStyles.Float,
                                       CultureInfo.InvariantCulture, out var health))
                    return true;

                uint takeId;
                lock (_companionTakeLock)
                {
                    do { takeId = (uint)Random.Shared.Next(1, int.MaxValue); }
                    while (_companionOpenTakes.ContainsKey(takeId));
                    _companionOpenTakes[takeId] = p[0];
                }

                _ = ExecLuaAsync(
                    $"if KCD2MP_LootTakeRegistered then KCD2MP_LootTakeRegistered(\"{takeId}\",\"{EscapeLua(p[0])}\",\"{cls:D}\",{amount},{health.ToString("R", CultureInfo.InvariantCulture)},\"{EscapeLua(p[4])}\") end");
                _ = SendLootTakeAsync(takeId, p[0], cls, amount, health);
                return true;
            }
        }

        return false;
    }

    private static float? TryNullableFloat(string text)
    {
        if (!float.TryParse(text, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var v))
            return null;
        return v < 0 ? null : v;
    }

    private readonly record struct LootManifestPacket(
        byte Phase, uint SnapshotId, string Source, Guid ItemClass,
        ushort Amount, float Health);

    private readonly record struct LootTakePacket(
        byte Claimer, bool Accepted, uint TakeId, string Source,
        Guid ItemClass, ushort Amount, float Health);

    private static bool TryParseLootManifest(
        byte[] payload, out LootManifestPacket packet)
    {
        packet = default;
        if (payload.Length < 1 + Protocol.LootManifestFixedBytes + 1)
            return false;

        ReadOnlySpan<byte> body = payload.AsSpan(1);
        byte phase = body[0];
        uint snapshotId =
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(1, 4));
        int sourceLength = body[5];
        if (body.Length != Protocol.LootManifestFixedBytes + sourceLength)
            return false;

        string source = Encoding.UTF8.GetString(body.Slice(6, sourceLength));
        int offset = 6 + sourceLength;
        var itemClass =
            new Guid(body.Slice(offset, Protocol.ItemClassLen));
        offset += Protocol.ItemClassLen;
        ushort amount =
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset, 2));
        offset += 2;
        float health =
            BinaryPrimitives.ReadSingleLittleEndian(body.Slice(offset, 4));

        packet = new LootManifestPacket(
            phase, snapshotId, source, itemClass, amount, health);
        return true;
    }

    private static bool TryParseLootTake(
        byte[] payload, out LootTakePacket packet)
    {
        packet = default;
        if (payload.Length < 2 + Protocol.LootTakeFixedBytes + 1)
            return false;

        byte claimer = payload[0];
        bool accepted = payload[1] != 0;
        ReadOnlySpan<byte> body = payload.AsSpan(2);
        uint takeId =
            BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(0, 4));
        int sourceLength = body[4];
        if (body.Length != Protocol.LootTakeFixedBytes + sourceLength)
            return false;

        string source = Encoding.UTF8.GetString(body.Slice(5, sourceLength));
        int offset = 5 + sourceLength;
        var itemClass =
            new Guid(body.Slice(offset, Protocol.ItemClassLen));
        offset += Protocol.ItemClassLen;
        ushort amount =
            BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset, 2));
        offset += 2;
        float health =
            BinaryPrimitives.ReadSingleLittleEndian(body.Slice(offset, 4));

        packet = new LootTakePacket(
            claimer, accepted, takeId, source, itemClass, amount, health);
        return true;
    }

    private async Task<bool> TryHandleCompanionPacketAsync(
        int type, byte[] payload, CancellationToken ct)
    {
        if (await TryHandlePartyPacketAsync(type, payload, ct)) return true;
        if (type == Protocol.HostRoleDown
            && payload.Length == Protocol.HostRoleDownPayloadLen)
        {
            bool host = payload[0] != 0;
            _campaignHostGhostId = payload[1];
            var campaignId = new Guid(payload.AsSpan(2, Protocol.CampaignIdLen));
            config.CampaignId = campaignId.ToString("N");
            Console.WriteLine(host
                ? "[companion] You are Henry / campaign host."
                : "[companion] You are a joining companion.");
            try
            {
                await ExecLuaAsync(
                    $"if KCD2MP_CompanionSetHost then KCD2MP_CompanionSetHost({(host ? "true" : "false")},{_campaignHostGhostId}) end");
            }
            catch { }
            return true;
        }

        if (type == Protocol.HostEndedDown
            && payload.Length == Protocol.HostEndedDownPayloadLen)
        {
            _sessionEndedByHost = true;
            Console.WriteLine("[companion] Henry/campaign host left. Session ended; authority will not migrate.");
            try
            {
                await ExecLuaAsync(
                    "if KCD2MP_ShowInteractionMsg then KCD2MP_ShowInteractionMsg(\"Henry left - companion session ended\") end");
            }
            catch { }
            try { _companionConnectionCts?.Cancel(); } catch { }
            return true;
        }

        if (type == Protocol.LootManifestDown)
        {
            if (!TryParseLootManifest(payload, out var packet))
                return true;

            string call = packet.Phase switch
            {
                Protocol.LootManifestPhaseBegin =>
                    $"KCD2MP_LootManifestBegin(\"{packet.SnapshotId}\",\"{EscapeLua(packet.Source)}\")",
                Protocol.LootManifestPhaseItem =>
                    $"KCD2MP_LootManifestItem(\"{packet.SnapshotId}\",\"{EscapeLua(packet.Source)}\",\"{packet.ItemClass:D}\",{packet.Amount},{packet.Health.ToString("R", CultureInfo.InvariantCulture)})",
                Protocol.LootManifestPhaseEnd =>
                    $"KCD2MP_LootManifestEnd(\"{packet.SnapshotId}\",\"{EscapeLua(packet.Source)}\")",
                _ => "",
            };
            if (call.Length > 0)
                try { await ExecLuaAsync($"if {call.Split('(')[0]} then {call} end"); } catch { }
            return true;
        }

        if (type == Protocol.LootTakeDown)
        {
            if (!TryParseLootTake(payload, out var packet))
                return true;

            bool mine = packet.Claimer == _myGhostId;
            if (mine)
            {
                lock (_companionTakeLock)
                    _companionOpenTakes.Remove(packet.TakeId);
            }

            try
            {
                await ExecLuaAsync(
                    $"if KCD2MP_LootTakeResolved then KCD2MP_LootTakeResolved(\"{packet.TakeId}\",{(packet.Accepted ? "true" : "false")},{(mine ? "true" : "false")},\"{EscapeLua(packet.Source)}\",\"{packet.ItemClass:D}\",{packet.Amount},{packet.Health.ToString("R", CultureInfo.InvariantCulture)}) end");
            }
            catch { }

            if (packet.Accepted && mine && !config.IsHosting)
                _ = CheckpointCompanionProfileAsync(CancellationToken.None);

            return true;
        }

        return false;
    }

    private async Task SendLootManifestAsync(
        byte phase, uint snapshotId, string source, Guid cls,
        ushort amount, float health)
    {
        var send = _companionSendPacket;
        if (send is null || !IsSafeLootSource(source)) return;

        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        var body = new byte[Protocol.LootManifestFixedBytes + sourceBytes.Length];
        body[0] = phase;
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(1, 4), snapshotId);
        body[5] = (byte)sourceBytes.Length;
        sourceBytes.CopyTo(body, 6);

        int o = 6 + sourceBytes.Length;
        cls.TryWriteBytes(body.AsSpan(o, Protocol.ItemClassLen)); o += Protocol.ItemClassLen;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o, 2), amount); o += 2;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(o, 4), health);

        await send(Protocol.LootManifestUp, body, CancellationToken.None);
    }

    private async Task SendLootTakeAsync(
        uint takeId, string source, Guid cls, ushort amount, float health)
    {
        var send = _companionSendPacket;
        if (send is null || !IsSafeLootSource(source)) return;

        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
        var body = new byte[Protocol.LootTakeFixedBytes + sourceBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(0, 4), takeId);
        body[4] = (byte)sourceBytes.Length;
        sourceBytes.CopyTo(body, 5);

        int o = 5 + sourceBytes.Length;
        cls.TryWriteBytes(body.AsSpan(o, Protocol.ItemClassLen)); o += Protocol.ItemClassLen;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(o, 2), amount); o += 2;
        BinaryPrimitives.WriteSingleLittleEndian(body.AsSpan(o, 4), health);

        await send(Protocol.LootTakeUp, body, CancellationToken.None);
    }

    private static bool IsSafeLootSource(string source)
    {
        if (source.Length is < 1 or > Protocol.MaxLootSourceNameLen) return false;
        foreach (char c in source)
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    private async Task StartCompanionSessionAsync(CancellationToken appCt)
    {
        var send = _companionSendPacket;
        if (send is null) return;

        _sessionEndedByHost = false;

        if (config.IsHosting)
        {
            Guid campaignId = Guid.TryParse(config.CampaignId, out var parsedCampaign)
                ? parsedCampaign : Guid.NewGuid();
            config.CampaignId = campaignId.ToString("N");
            var hostClaim = new byte[Protocol.CampaignIdLen];
            campaignId.TryWriteBytes(hostClaim);
            await send(Protocol.HostClaimUp, hostClaim, appCt);
            try
            {
                await ExecLuaAsync(
                    "if KCD2MP_CompanionSetHost then KCD2MP_CompanionSetHost(true) end");
            }
            catch { }
            return;
        }

        try
        {
            await ExecLuaAsync(
                "if KCD2MP_CompanionSetHost then KCD2MP_CompanionSetHost(false) end");
        }
        catch { }

        if (_transport is not LogTailGameTransport)
        {
            Console.WriteLine(
                "[companion-profile] persistent inventory requires the default logtail transport; profile swap disabled on HTTP fallback.");
            return;
        }

        await _companionProfileGate.WaitAsync(appCt);
        try
        {
            // Restore a save-directory shield left by a previous crash before
            // taking a fresh snapshot. This makes guest autosaves reversible.
            if (_companionSaveShield.FindLatestPending(config.CompanionId) is { } staleShield)
            {
                Console.WriteLine("[save-shield] restoring protected KCD2 saves from a previous interrupted session.");
                _companionSaveShield.Restore(staleShield);
            }
            _activeSaveShield = _companionSaveShield.Begin(config.CompanionId);

            // If a previous agent died while the co-op inventory was installed,
            // repair the ordinary save inventory before taking a fresh backup.
            if (_companionProfileStore.LoadRecovery(config) is { } staleRecovery)
            {
                Console.WriteLine("[companion-profile] stale recovery backup found; restoring ordinary inventory first.");
                await ApplyInventorySnapshotAsync(staleRecovery, appCt);
                _companionProfileStore.DeleteRecovery(config);
            }

            var backup = await CaptureInventorySnapshotAsync("singleplayer", appCt);
            if (backup is null) return;

            _singlePlayerInventoryBackup = backup;
            _companionProfileStore.SaveRecovery(config, backup);

            var profile = _companionProfileStore.LoadProfile(config);
            if (profile is null)
            {
                // First co-op session: seed the companion from what the player
                // is wearing/carrying now. From this point onward it becomes an
                // independent external profile.
                profile = backup with
                {
                    SavedUtc = DateTime.UtcNow,
                    CampaignKey = _companionProfileStore.CampaignKey(config),
                };
                _companionProfileStore.SaveProfile(config, profile);
                Console.WriteLine("[companion-profile] first session: seeded companion inventory from current save.");
            }
            else
            {
                await ApplyInventorySnapshotAsync(profile, appCt);
                Console.WriteLine($"[companion-profile] restored {profile.Items.Length} inventory row(s) and {profile.Money:F0} groschen.");
            }

            _companionInventoryActive = true;
            _companionCheckpointCts = CancellationTokenSource.CreateLinkedTokenSource(appCt);
            _companionCheckpointTask = Task.Run(
                () => CompanionCheckpointLoopAsync(_companionCheckpointCts.Token),
                CancellationToken.None);
        }
        finally
        {
            _companionProfileGate.Release();
        }
    }

    private async Task StopCompanionSessionAsync()
    {
        if (config.IsHosting) return;

        try { _companionCheckpointCts?.Cancel(); } catch { }
        if (_companionCheckpointTask is not null)
        {
            try { await _companionCheckpointTask; } catch { }
        }
        _companionCheckpointCts?.Dispose();
        _companionCheckpointCts = null;
        _companionCheckpointTask = null;

        if (!_companionInventoryActive || _singlePlayerInventoryBackup is null)
            return;

        await _companionProfileGate.WaitAsync();
        try
        {
            try { await CheckpointCompanionProfileCoreAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                Console.WriteLine($"[companion-profile] final checkpoint failed: {ex.Message}");
            }

            try
            {
                await ApplyInventorySnapshotAsync(
                    _singlePlayerInventoryBackup, CancellationToken.None);
                _companionProfileStore.DeleteRecovery(config);
                if (_activeSaveShield is not null)
                {
                    _companionSaveShield.Restore(_activeSaveShield);
                    _activeSaveShield = null;
                    Console.WriteLine("[save-shield] guest save directory restored; co-op autosaves discarded.");
                }
                Console.WriteLine("[companion-profile] ordinary single-player inventory restored.");
                _companionInventoryActive = false;
                _singlePlayerInventoryBackup = null;
            }
            catch (Exception ex)
            {
                // Recovery file deliberately remains on disk.
                Console.WriteLine(
                    $"[companion-profile] restore failed: {ex.Message}. Recovery backup retained for next start.");
            }
        }
        finally
        {
            _companionProfileGate.Release();
        }
    }

    private async Task CompanionCheckpointLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
            catch (OperationCanceledException) { break; }
            try { await CheckpointCompanionProfileAsync(ct); }
            catch (Exception ex)
            {
                Console.WriteLine($"[companion-profile] checkpoint failed: {ex.Message}");
            }
        }
    }

    private async Task CheckpointCompanionProfileAsync(CancellationToken ct)
    {
        if (!_companionInventoryActive || config.IsHosting) return;
        if (!await _companionProfileGate.WaitAsync(0, ct)) return;
        try { await CheckpointCompanionProfileCoreAsync(ct); }
        finally { _companionProfileGate.Release(); }
    }

    private async Task CheckpointCompanionProfileCoreAsync(CancellationToken ct)
    {
        var snap = await CaptureInventorySnapshotAsync("companion", ct);
        if (snap is not null)
            _companionProfileStore.SaveProfile(config, snap);
    }

    private async Task<CompanionInventorySnapshot?> CaptureInventorySnapshotAsync(
        string purpose, CancellationToken ct)
    {
        string tag = $"{purpose}_{Guid.NewGuid():N}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));

        Task<CompanionSnapshotCollector.RawSnapshot> wait =
            _companionSnapshots.WaitAsync(tag, timeout.Token);

        await _transport.ExecuteNowAsync(
            $"if KCD2MP_CompanionSnapshot then KCD2MP_CompanionSnapshot(\"{tag}\") end",
            timeout.Token);

        CompanionSnapshotCollector.RawSnapshot raw;
        try { raw = await wait; }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[companion-profile] snapshot '{purpose}' timed out.");
            return null;
        }

        Guid[] equipped;
        try { equipped = await _transport.ReadEquippedItemClassesAsync(timeout.Token); }
        catch { equipped = []; }

        string player = string.IsNullOrWhiteSpace(config.PlayerName)
            ? Environment.MachineName
            : config.PlayerName!;

        return new CompanionInventorySnapshot(
            SchemaVersion: 1,
            SavedUtc: DateTime.UtcNow,
            PlayerName: player,
            CampaignKey: _companionProfileStore.CampaignKey(config),
            Money: raw.Money,
            Health: raw.Health,
            Stamina: raw.Stamina,
            Items: raw.Items,
            Equipped: equipped,
            Identity: new CompanionIdentity(
                config.CompanionName, config.CompanionFaceIndex,
                config.CompanionSex, config.CompanionBackground),
            Progression: new CompanionProgression(
                raw.Skills, DateTime.UtcNow));
    }

    private async Task ApplyInventorySnapshotAsync(
        CompanionInventorySnapshot snapshot, CancellationToken ct)
    {
        await _transport.ExecuteNowAsync(
            "if KCD2MP_CompanionClearInventory then KCD2MP_CompanionClearInventory() end", ct);

        foreach (var item in snapshot.Items)
        {
            int amount = Math.Clamp(item.Amount, 1, ushort.MaxValue);
            string hp = Math.Clamp(item.Health, 0f, 10f)
                .ToString("R", CultureInfo.InvariantCulture);
            await ExecLuaAsync(
                $"if KCD2MP_CompanionAddItem then KCD2MP_CompanionAddItem(\"{item.ItemClass:D}\",{hp},{amount}) end");
        }

        await ExecLuaAsync(
            $"if KCD2MP_CompanionSetMoney then KCD2MP_CompanionSetMoney({snapshot.Money.ToString("R", CultureInfo.InvariantCulture)}) end");
        await _transport.FlushAsync(ct);

        // Creation and equipment are separate in KCD2. Restore the equipped
        // class set after all inventory rows exist.
        if (snapshot.Progression is not null)
        {
            foreach (var kv in snapshot.Progression.Levels)
            {
                if (!CompanionStateNames.Allowed.Contains(kv.Key)) continue;
                try { await _transport.SetPlayerStateAsync(kv.Key, kv.Value, ct); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[companion-profile] progression '{kv.Key}' could not be reapplied on this build: {ex.Message}");
                }
            }
        }

        foreach (Guid cls in snapshot.Equipped.Distinct())
        {
            try { await _transport.EquipItemOnPlayerAsync(cls, ct); }
            catch (Exception ex)
            {
                Console.WriteLine($"[companion-profile] equip {cls} failed: {ex.Message}");
            }
        }
    }
}
