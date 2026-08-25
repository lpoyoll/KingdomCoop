using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace KcdMp.Client;

public partial class GameBridge
{
    private byte _campaignHostGhostId;
    private byte _localPartyState = Protocol.PartyAlive;
    private readonly Dictionary<byte, byte> _partyStates = [];
    private bool _partyWiped;
    private uint _partyWipeGeneration;

    private (float X,float Y,float Z,float Rot,DateTime At)? _lastHostTransitionSample;
    private bool _lastDialog;
    private DateTime _lastProgressPollUtc = DateTime.MinValue;

    private uint _tradeSessionId;
    private byte _tradePeer;
    private readonly List<(Guid Class, ushort Amount, float Health)> _tradeOffer = [];
    private float _tradeMoney;

    private async Task StartPartySystemsAsync(CancellationToken ct)
    {
        config.NormaliseCompanionSettings();
        await SendIdentityAsync(ct);
        try
        {
            await ExecLuaAsync(
                $"if KCD2MP_CompanionIdentityLocal then KCD2MP_CompanionIdentityLocal(\"{EscapeLua(config.CompanionName)}\",{config.CompanionFaceIndex},\"{EscapeLua(config.CompanionSex)}\",\"{EscapeLua(config.CompanionBackground)}\") end");
        }
        catch { }
    }

    private Task StopPartySystemsAsync()
    {
        _partyStates.Clear();
        _localPartyState = Protocol.PartyAlive;
        _partyWiped = false;
        _tradeSessionId = 0;
        _tradeOffer.Clear();
        return Task.CompletedTask;
    }

    private bool HandlePartyGameEvent(string name, string arg)
    {
        switch (name)
        {
            case "catch_up":
                _ = CatchUpToHostAsync();
                return true;

            case "revive":
                if (byte.TryParse(arg.Trim(), out byte target))
                    _ = SendSimpleAsync(Protocol.ReviveUp, [target]);
                return true;

            case "crime_auto":
            {
                var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                byte kind = p.Length > 0 && byte.TryParse(p[0], out var k) ? k : Protocol.CrimeGeneric;
                byte severity = p.Length > 1 && byte.TryParse(p[1], out var sev) ? sev : (byte)1;
                _ = SendSimpleAsync(Protocol.CrimeUp, [kind, severity]);
                return true;
            }

            case "party_ping":
            {
                var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 3
                    && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
                    && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
                    && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                {
                    var b = new byte[12];
                    BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(0,4), x);
                    BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4,4), y);
                    BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8,4), z);
                    _ = SendSimpleAsync(Protocol.PartyPingUp, b);
                }
                return true;
            }

            case "trade_begin":
                if (byte.TryParse(arg.Trim(), out var peer))
                    _ = SendSimpleAsync(Protocol.TradeBeginUp, [peer]);
                return true;

            case "trade_add":
            {
                var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 2 && Guid.TryParse(p[0], out var cls)
                    && ushort.TryParse(p[1], out var amount))
                {
                    float hp = p.Length >= 3
                        && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var h)
                            ? h : 1f;
                    if (_tradeOffer.Count < Protocol.MaxTradeItems)
                        _tradeOffer.Add((cls, amount, hp));
                    _ = SendTradeOfferAsync();
                }
                return true;
            }

            case "trade_clear":
                _tradeOffer.Clear(); _tradeMoney = 0; _ = SendTradeOfferAsync(); return true;

            case "trade_money":
                if (float.TryParse(arg.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var money))
                { _tradeMoney = Math.Max(0, money); _ = SendTradeOfferAsync(); }
                return true;

            case "trade_accept":
                if (_tradeSessionId != 0)
                {
                    var b = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(b, _tradeSessionId);
                    _ = SendSimpleAsync(Protocol.TradeAcceptUp, b);
                }
                return true;

            case "trade_cancel":
                if (_tradeSessionId != 0)
                {
                    var b = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(b, _tradeSessionId);
                    _ = SendSimpleAsync(Protocol.TradeCancelUp, b);
                }
                return true;

            case "comp_profile_skill":
            {
                var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length >= 3
                    && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    _companionSnapshots.Skill(p[0], p[1], v);
                return true;
            }
        }
        return false;
    }

    private async Task<bool> TryHandlePartyPacketAsync(int type, byte[] payload, CancellationToken ct)
    {
        if (type == Protocol.TransitionDown && payload.Length == Protocol.TransitionDownPayloadLen)
        {
            byte source = payload[0];
            if (source != _campaignHostGhostId) return true;
            byte kind = payload[1];
            float x = BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(2,4));
            float y = BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(6,4));
            float z = BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(10,4));
            float r = BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(14,4));
            if (!config.IsHosting)
                await ApplyTransitionAsync(kind, x,y,z,r);
            return true;
        }

        if (type == Protocol.PartyStateDown && payload.Length == 2)
        {
            byte who = payload[0], state = payload[1];
            _partyStates[who] = state;
            try { await ExecLuaAsync($"if KCD2MP_PartyState then KCD2MP_PartyState({who},{state}) end"); } catch { }
            return true;
        }

        if (type == Protocol.ReviveDown && payload.Length == 3)
        {
            byte reviver=payload[0], target=payload[1]; bool ok=payload[2]!=0;
            if (ok && target == _myGhostId)
            {
                _localPartyState = Protocol.PartyAlive;
                await SetPlayerHealthVerifiedAsync(config.CompanionReviveHealth, ct);
                try { await ExecLuaAsync("if KCD2MP_SetLocalDowned then KCD2MP_SetLocalDowned(false) end"); } catch { }
            }
            return true;
        }

        if (type == Protocol.PartyWipeDown && payload.Length == 4)
        {
            _partyWiped = true;
            _partyWipeGeneration = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            if (config.IsHosting)
            {
                await SendPartyStateAsync(Protocol.PartyReloading);
                try { await ExecLuaAsync("if KCD2MP_PartyWipeHost then KCD2MP_PartyWipeHost() end"); } catch { }
            }
            else
            {
                try { await ExecLuaAsync("if KCD2MP_PartyWipeGuest then KCD2MP_PartyWipeGuest() end"); } catch { }
            }
            return true;
        }

        if (type == Protocol.PartyResumeDown && payload.Length == Protocol.PartyResumeDownPayloadLen)
        {
            uint gen=BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0,4));
            if (gen != _partyWipeGeneration) return true;
            float x=BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(4,4));
            float y=BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(8,4));
            float z=BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(12,4));
            float r=BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(16,4));
            _partyWiped=false;
            _localPartyState=Protocol.PartyAlive;
            await SetPlayerHealthVerifiedAsync(config.CompanionReviveHealth, ct);
            if (!config.IsHosting) await ApplyTransitionAsync(Protocol.TransitionKindReload,x,y,z,r);
            try { await ExecLuaAsync("if KCD2MP_PartyResume then KCD2MP_PartyResume() end"); } catch { }
            return true;
        }

        if (type == Protocol.DialogueDown && payload.Length >= 3)
        {
            bool active=payload[1]!=0; int n=payload[2];
            string label = n>0 && payload.Length >= 3+n ? Encoding.UTF8.GetString(payload,3,n) : "Henry";
            try { await ExecLuaAsync($"if KCD2MP_DialogueSpectator then KCD2MP_DialogueSpectator({(active?"true":"false")},\"{EscapeLua(label)}\") end"); } catch { }
            return true;
        }

        if (type == Protocol.CrimeDown && payload.Length == 3)
        {
            byte offender=payload[0], kind=payload[1], sev=payload[2];
            try { await ExecLuaAsync($"if KCD2MP_CompanionCrime then KCD2MP_CompanionCrime({offender},{kind},{sev}) end"); } catch { }
            return true;
        }

        if (type == Protocol.IdentityDown && payload.Length >= 5)
        {
            byte source=payload[0]; int face=payload[1]; int sex=payload[2]; int nameLen=payload[3];
            if (4+nameLen >= payload.Length) return true;
            string name=Encoding.UTF8.GetString(payload,4,nameLen);
            int bgLen=payload[4+nameLen];
            string bg=bgLen>0 && 5+nameLen+bgLen<=payload.Length
                ? Encoding.UTF8.GetString(payload,5+nameLen,bgLen) : "";
            try { await ExecLuaAsync($"if KCD2MP_CompanionIdentityRemote then KCD2MP_CompanionIdentityRemote({source},{face},{sex},\"{EscapeLua(name)}\",\"{EscapeLua(bg)}\") end"); } catch { }
            return true;
        }

        if (type == Protocol.TradeStateDown && payload.Length >= 6)
        {
            byte kind=payload[0]; uint session=BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(1,4)); byte peer=payload[5];
            if (kind==0) { _tradeSessionId=session; _tradePeer=peer; _tradeOffer.Clear(); _tradeMoney=0; }
            try { await ExecLuaAsync($"if KCD2MP_TradeState then KCD2MP_TradeState({kind},\"{session}\",{peer}) end"); } catch { }
            return true;
        }

        if (type == Protocol.TradeCommitDown && payload.Length >= 14)
        {
            await ApplyTradeCommitAsync(payload, ct);
            return true;
        }

        if (type == Protocol.TradeCancelDown && payload.Length == 5)
        {
            _tradeSessionId=0; _tradePeer=0; _tradeOffer.Clear(); _tradeMoney=0;
            try { await ExecLuaAsync("if KCD2MP_TradeCancelled then KCD2MP_TradeCancelled() end"); } catch { }
            return true;
        }

        if (type == Protocol.PartyPingDown && payload.Length == Protocol.PartyPingDownPayloadLen)
        {
            byte source=payload[0];
            float x=BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(1,4));
            float y=BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(5,4));
            float z=BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(9,4));
            try { await ExecLuaAsync($"if KCD2MP_PartyPingRemote then KCD2MP_PartyPingRemote({source},{x.ToString("R",CultureInfo.InvariantCulture)},{y.ToString("R",CultureInfo.InvariantCulture)},{z.ToString("R",CultureInfo.InvariantCulture)}) end"); } catch { }
            return true;
        }
        return false;
    }

    private async Task CompanionPartyTickAsync(PlayerState st, CancellationToken ct)
    {
        // Soft-down BEFORE ordinary KCD2 death when a survivable low-health
        // sample is observed. Actual unconscious state also counts.
        bool shouldDown = st.IsUnconscious == true
            || (st.Health is float h && h > 0 && h <= config.CompanionDownedHealth);
        if (shouldDown && _localPartyState == Protocol.PartyAlive)
        {
            _localPartyState = Protocol.PartyDowned;
            await SetPlayerHealthVerifiedAsync(Math.Max(1f, config.CompanionDownedHealth), ct);
            try { await ExecLuaAsync("if KCD2MP_SetLocalDowned then KCD2MP_SetLocalDowned(true) end"); } catch { }
            await SendPartyStateAsync(Protocol.PartyDowned);
        }

        // If KCD2 itself reports unconscious, stay down until a network revive.
        if (_localPartyState == Protocol.PartyDowned) return;

        if (config.IsHosting)
        {
            await HostTransitionTickAsync(st, ct);
            await HostDialogueTickAsync(ct);

            // A completed host reload after a wipe is visible as alive state
            // and a valid world position while we were in reloading state.
            if (_partyWiped && st.IsDead == false && st.Health is > 1)
            {
                await SendPartyResumeAsync(st, ct);
            }
        }

        if (!config.IsHosting && DateTime.UtcNow - _lastProgressPollUtc > TimeSpan.FromSeconds(15))
        {
            _lastProgressPollUtc = DateTime.UtcNow;
            _ = CheckpointCompanionProfileAsync(CancellationToken.None);
        }
    }

    private async Task HostTransitionTickAsync(PlayerState st, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_lastHostTransitionSample is { } prev)
        {
            float dx=st.X-prev.X, dy=st.Y-prev.Y, dz=st.Z-prev.Z;
            float d2=dx*dx+dy*dy+dz*dz;
            double sec=(now-prev.At).TotalSeconds;
            if (sec < 3 && d2 > 35*35)
                await SendTransitionAsync(Protocol.TransitionKindJump, st.X,st.Y,st.Z,st.RotZ,ct);
        }
        _lastHostTransitionSample=(st.X,st.Y,st.Z,st.RotZ,now);
    }

    private async Task HostDialogueTickAsync(CancellationToken ct)
    {
        bool dialog=false;
        try
        {
            // Verified KCD2 scriptbind. The result is returned through a
            // one-shot log event because ExecuteString has no direct return.
            await _transport.ExecuteNowAsync(
                "if KCD2MP_ReportDialogue then KCD2MP_ReportDialogue() end", ct);
            return; // event path sends; kept deliberately asynchronous
        }
        catch { }
    }

    private async Task SendTransitionAsync(byte kind,float x,float y,float z,float rot,CancellationToken ct)
    {
        var b=new byte[Protocol.TransitionUpPayloadLen]; b[0]=kind;
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(1,4),x);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(5,4),y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(9,4),z);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(13,4),rot);
        await SendSimpleAsync(Protocol.TransitionUp,b,ct);
    }

    private async Task ApplyTransitionAsync(byte kind,float x,float y,float z,float rot)
    {
        try { await ExecLuaAsync($"if KCD2MP_CompanionTransition then KCD2MP_CompanionTransition({kind},{F(x)},{F(y)},{F(z)},{F(rot)}) end"); } catch { }
    }

    private async Task CatchUpToHostAsync()
    {
        try { await ExecLuaAsync($"if KCD2MP_CatchUpToHenry then KCD2MP_CatchUpToHenry({F(config.CompanionCatchUpDistance)}) end"); } catch { }
    }

    private async Task SendPartyStateAsync(byte state) =>
        await SendSimpleAsync(Protocol.PartyStateUp,[state]);

    private async Task SendPartyResumeAsync(PlayerState st,CancellationToken ct)
    {
        var b=new byte[Protocol.PartyResumeUpPayloadLen];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0,4),_partyWipeGeneration);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4,4),st.X);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8,4),st.Y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(12,4),st.Z);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(16,4),st.RotZ);
        await SendSimpleAsync(Protocol.PartyResumeUp,b,ct);
        _partyWiped=false; _localPartyState=Protocol.PartyAlive;
        await SendPartyStateAsync(Protocol.PartyAlive);
    }

    private async Task SendIdentityAsync(CancellationToken ct)
    {
        byte[] name=Encoding.UTF8.GetBytes(config.CompanionName);
        if(name.Length>Protocol.MaxCompanionNameLen) name=name[..Protocol.MaxCompanionNameLen];
        byte[] bg=Encoding.UTF8.GetBytes(config.CompanionBackground);
        if(bg.Length>Protocol.MaxBackgroundLen) bg=bg[..Protocol.MaxBackgroundLen];
        byte sex=config.CompanionSex.StartsWith("f",StringComparison.OrdinalIgnoreCase)?(byte)1:(byte)0;
        var b=new byte[4+name.Length+bg.Length];
        b[0]=(byte)Math.Clamp(config.CompanionFaceIndex,0,47); b[1]=sex; b[2]=(byte)name.Length;
        name.CopyTo(b,3); b[3+name.Length]=(byte)bg.Length; bg.CopyTo(b,4+name.Length);
        await SendSimpleAsync(Protocol.IdentityUp,b,ct);
    }

    private async Task SendTradeOfferAsync()
    {
        if(_tradeSessionId==0) return;
        int n=Math.Min(_tradeOffer.Count,Protocol.MaxTradeItems);
        var b=new byte[9+n*Protocol.TradeItemLen];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0,4),_tradeSessionId);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4,4),_tradeMoney);
        b[8]=(byte)n; int o=9;
        foreach(var it in _tradeOffer.Take(n))
        {
            it.Class.TryWriteBytes(b.AsSpan(o,16)); o+=16;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o,2),it.Amount); o+=2;
            BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o,4),it.Health); o+=4;
        }
        await SendSimpleAsync(Protocol.TradeOfferUp,b);
    }

    private async Task ApplyTradeCommitAsync(byte[] payload,CancellationToken ct)
    {
        uint session=BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(0,4));
        int o=4;
        var own=ReadOffer(payload,ref o);
        var peer=ReadOffer(payload,ref o);
        if(own is null || peer is null) return;

        // The Lua apply is idempotency-gated by session id and verifies local
        // quantities again immediately before removing them.
        var sb=new StringBuilder();
        sb.Append($"if KCD2MP_TradeCommitBegin then KCD2MP_TradeCommitBegin(\"{session}\",{F(own.Value.Money)},{F(peer.Value.Money)}) end;");
        foreach(var x in own.Value.Items)
            sb.Append($"if KCD2MP_TradeCommitGive then KCD2MP_TradeCommitGive(\"{x.Class:D}\",{x.Amount},{F(x.Health)}) end;");
        foreach(var x in peer.Value.Items)
            sb.Append($"if KCD2MP_TradeCommitReceive then KCD2MP_TradeCommitReceive(\"{x.Class:D}\",{x.Amount},{F(x.Health)}) end;");
        sb.Append("if KCD2MP_TradeCommitEnd then KCD2MP_TradeCommitEnd() end;");
        await _transport.ExecuteNowAsync(sb.ToString(),ct);
        _tradeSessionId=0; _tradePeer=0; _tradeOffer.Clear(); _tradeMoney=0;
        if(!config.IsHosting) _=CheckpointCompanionProfileAsync(CancellationToken.None);
    }

    private static (float Money,(Guid Class,ushort Amount,float Health)[] Items)? ReadOffer(byte[] b,ref int o)
    {
        if(o+5>b.Length) return null;
        float money=BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o,4)); o+=4;
        int n=b[o++]; if(n>Protocol.MaxTradeItems || o+n*Protocol.TradeItemLen>b.Length) return null;
        var items=new (Guid,ushort,float)[n];
        for(int i=0;i<n;i++)
        {
            var cls=new Guid(b.AsSpan(o,16));o+=16;
            ushort amount=BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o,2));o+=2;
            float hp=BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o,4));o+=4;
            items[i]=(cls,amount,hp);
        }
        return(money,items);
    }

    private async Task SendSimpleAsync(byte type, byte[] body,CancellationToken ct=default)
    {
        var send=_companionSendPacket; if(send is null) return;
        await send(type,body,ct);
    }

    private async Task SetPlayerHealthVerifiedAsync(float health,CancellationToken ct)
    {
        try { await _transport.SetPlayerStateAsync("health",health,ct); }
        catch(Exception ex){ Console.WriteLine($"[party] health write failed: {ex.Message}"); }
    }

    private static string F(float v)=>v.ToString("R",CultureInfo.InvariantCulture);
}
