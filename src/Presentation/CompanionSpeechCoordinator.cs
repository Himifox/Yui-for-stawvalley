using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace YuiToIssho;

internal static class SpeechPriorities
{
    public const int Normal = 1;
    public const int Important = 2;
    public const int Critical = 3;

    public static bool IsValid(int value) => value is >= Normal and <= Critical;
}

internal static class SpeechEventContracts
{
    public const int MaximumTextCharacters = 120;
    public const int MaximumIdCharacters = 64;
    public const int MaximumLifetimeTicks = 360;

    public static bool IsValid(SpeechEventDto? speech, string epoch)
    {
        return speech is not null
            && speech.ProtocolVersion == MultiplayerProtocol.Version
            && speech.SessionEpoch == epoch
            && Guid.TryParseExact(speech.EventId, "N", out _)
            && speech.SpeechSequence > 0
            && speech.OwnerId != 0
            && CompanionIdentity.IsValidSlot(speech.Slot)
            && speech.BodyGeneration > 0
            && IsBoundedText(speech.SpeechId, MaximumIdCharacters)
            && IsBoundedText(speech.TopicKey, MaximumIdCharacters)
            && IsBoundedText(speech.Text, MaximumTextCharacters)
            && SpeechPriorities.IsValid(speech.Priority)
            && speech.StartedAtHostTick > 0
            && speech.ExpiresAtHostTick > speech.StartedAtHostTick
            && speech.ExpiresAtHostTick - speech.StartedAtHostTick <= MaximumLifetimeTicks;
    }

    public static bool IsValidSnapshot(CompanionSnapshotDto state)
    {
        if (state.SpeechSequence == 0)
        {
            return state.SpeechBodyGeneration == 0
                && string.IsNullOrEmpty(state.SpeechId)
                && string.IsNullOrEmpty(state.SpeechTopicKey)
                && string.IsNullOrEmpty(state.SpeechText)
                && state.SpeechPriority == 0
                && state.SpeechRemainingTicks == 0;
        }
        return state.SpeechBodyGeneration > 0
            && IsBoundedText(state.SpeechId, MaximumIdCharacters)
            && IsBoundedText(state.SpeechTopicKey, MaximumIdCharacters)
            && IsBoundedText(state.SpeechText, MaximumTextCharacters)
            && SpeechPriorities.IsValid(state.SpeechPriority)
            && state.SpeechRemainingTicks is > 0 and <= MaximumLifetimeTicks;
    }

    private static bool IsBoundedText(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximum
        && !value.Any(char.IsControl);
}

internal readonly record struct CompanionSpeechSnapshot(
    ulong Sequence,
    ulong BodyGeneration,
    string SpeechId,
    string TopicKey,
    string Text,
    int Priority,
    int RemainingTicks);

internal sealed class CompanionSpeechCoordinator
{
    private const int DisplayTicks = 240;
    private const int CandidateLifetimeTicks = 180;
    private const int CommandCooldownTicks = 180;
    private const int TaskCooldownTicks = 480;
    private const int FailureCooldownTicks = 300;
    private const int TextRepeatCooldownTicks = 600;
    private const int AmbientTopicCooldownTicks = 1800;
    private const int AmbientInitialDelayTicks = 1200;
    private const int AmbientInitialVarianceTicks = 900;
    private const int AmbientIntervalTicks = 3600;
    private const int AmbientIntervalVarianceTicks = 1800;
    private const int AmbientMaximumTileDistance = 8;
    private const int BubbleFadeTicks = 15;
    private readonly ITranslationHelper translation;
    private readonly CompanionRegistry registry;
    private readonly CompanionBodyBinder bodies;
    private readonly CompanionMultiplayerCoordinator multiplayer;
    private readonly IMonitor monitor;
    private readonly Dictionary<CompanionIdentity, SpeechRuntime> runtimes = new();
    private ulong currentTick;
    private string clientEpoch = string.Empty;

    public CompanionSpeechCoordinator(IModHelper helper, CompanionRegistry registry, CompanionBodyBinder bodies, CompanionMultiplayerCoordinator multiplayer, IMonitor monitor)
    {
        this.translation = helper.Translation;
        this.registry = registry;
        this.bodies = bodies;
        this.multiplayer = multiplayer;
        this.monitor = monitor;
    }

    public void ObserveCommandSettlement(CommandReceiptObservation observation)
    {
        if (!Context.IsMainPlayer || IsReadOnlyCommand(observation.Command))
            return;
        if (!observation.Result.IsSuccess)
        {
            this.OfferFailure(observation.Identity, $"command:{observation.RequestId}", observation.Result.Code);
            return;
        }
        string? key = observation.Command switch
        {
            "summon" => "speech.command.summon",
            "follow" => "speech.command.follow",
            "wait" => "speech.command.wait",
            "sit" => "speech.command.sit",
            "stand" => "speech.command.stand",
            "hug" => "speech.command.hug",
            "gift" => observation.Result.Code switch
            {
                "GIFT-LOVED" => "speech.gift.loved",
                "GIFT-LIKED" => "speech.gift.liked",
                "GIFT-DISLIKED" => "speech.gift.disliked",
                "GIFT-HATED" => "speech.gift.hated",
                _ => "speech.gift.neutral",
            },
            "stop" => "speech.command.stop",
            "work-start" => "speech.command.work-start",
            "work-stop" => "speech.command.work-stop",
            "work-resume" => "speech.command.work-resume",
            "assist-start" => "speech.command.assist-start",
            "assist-stop" => "speech.command.assist-stop",
            _ => null,
        };
        if (key is not null)
            this.Offer(observation.Identity, key, $"command.{observation.Command}", SpeechPriorities.Important, CommandCooldownTicks, $"command:{observation.RequestId}");
    }

    public void ObserveTaskCompletion(TaskCompletionObservation observation)
    {
        if (!Context.IsMainPlayer)
            return;
        if (!observation.Result.IsSuccess)
        {
            if (!IsSilentCancellation(observation.Result.Code))
                this.OfferFailure(observation.Identity, $"task:{observation.OperationId}", observation.Result.Code);
            return;
        }
        string task = NormalizeTaskKind(observation.TaskKind);
        this.Offer(
            observation.Identity,
            $"speech.task.success.{task}",
            $"task.success.{task}",
            SpeechPriorities.Normal,
            TaskCooldownTicks,
            $"task:{observation.OperationId}");
    }

    public void OfferFirstMeeting(CompanionIdentity identity)
    {
        if (!Context.IsMainPlayer)
            return;
        this.Offer(identity, "speech.companion.first-meeting", "companion.first-meeting", SpeechPriorities.Important, CommandCooldownTicks, $"first-meeting:{identity.OwnerId}");
    }

    public void ObserveNaturalAssistStarted(CompanionIdentity identity, string kind)
    {
        if (!Context.IsMainPlayer)
            return;
        string? key = kind switch
        {
            WorkKinds.Chop => "speech.assist.join.chopping",
            WorkKinds.Mow => "speech.assist.join.mowing",
            _ => null,
        };
        if (key is not null)
            this.Offer(identity, key, $"assist.join.{kind}", SpeechPriorities.Important, TaskCooldownTicks, $"assist:{kind}:{this.currentTick}");
    }

    public void AcceptNetworkSpeech(SpeechEventDto speech)
    {
        if (Context.IsMainPlayer || !SpeechEventContracts.IsValid(speech, this.multiplayer.SessionEpoch))
            return;
        this.EnsureClientEpoch();
        this.AcceptRemote(
            new CompanionIdentity(speech.OwnerId, speech.Slot),
            speech.SpeechSequence,
            speech.BodyGeneration,
            speech.SpeechId,
            speech.TopicKey,
            speech.Text,
            speech.Priority,
            (int)(speech.ExpiresAtHostTick - speech.StartedAtHostTick));
    }

    public void AcceptSnapshotSpeech(CompanionSnapshotDto state)
    {
        if (Context.IsMainPlayer || state.SpeechSequence == 0 || !SpeechEventContracts.IsValidSnapshot(state))
            return;
        this.EnsureClientEpoch();
        this.AcceptRemote(
            state.Identity,
            state.SpeechSequence,
            state.SpeechBodyGeneration,
            state.SpeechId,
            state.SpeechTopicKey,
            state.SpeechText,
            state.SpeechPriority,
            state.SpeechRemainingTicks);
    }

    public CompanionSpeechSnapshot? GetSnapshot(CompanionIdentity identity)
    {
        if (!this.runtimes.TryGetValue(identity, out SpeechRuntime? runtime)
            || runtime.Current is not ActiveSpeech speech
            || speech.ExpiresAtTick <= this.currentTick
            || !this.bodies.TryGetBodyGeneration(identity, out ulong currentGeneration)
            || currentGeneration != speech.BodyGeneration)
            return null;
        return new CompanionSpeechSnapshot(
            speech.Sequence,
            speech.BodyGeneration,
            speech.SpeechId,
            speech.TopicKey,
            speech.Text,
            speech.Priority,
            (int)Math.Min((ulong)SpeechEventContracts.MaximumLifetimeTicks, speech.ExpiresAtTick - this.currentTick));
    }

    public string BuildInteractionLine(CompanionMenuIdentitySnapshot identity)
    {
        SpeechRuntime runtime = this.GetRuntime(identity.Identity);
        runtime.InteractionCount++;
        IReadOnlyList<string> topics = BuildInteractionTopics(identity);
        string key = topics[(int)((runtime.InteractionCount - 1) % (ulong)topics.Count)];
        return BoundText(this.TranslateVariant(key, identity.Identity, key, $"interaction:{runtime.InteractionCount}"));
    }

    public void Update(ulong tick)
    {
        this.currentTick = tick;
        if (Context.IsMainPlayer)
        {
            this.ObserveVitalChanges();
            this.ObserveAmbientSpeech();
        }
        foreach ((CompanionIdentity identity, SpeechRuntime runtime) in this.runtimes.ToArray())
        {
            if (runtime.Current is ActiveSpeech current && current.ExpiresAtTick <= tick)
                runtime.Current = null;
            foreach (string topic in runtime.TopicCooldowns.Where(pair => pair.Value <= tick).Select(pair => pair.Key).ToArray())
                runtime.TopicCooldowns.Remove(topic);
            foreach (string text in runtime.TextCooldowns.Where(pair => pair.Value <= tick).Select(pair => pair.Key).ToArray())
                runtime.TextCooldowns.Remove(text);
            if (runtime.Pending is not SpeechCandidate pending)
                continue;
            if (pending.ExpiresAtTick <= tick)
            {
                runtime.Pending = null;
                continue;
            }
            if (runtime.Current is ActiveSpeech active && active.Priority >= pending.Priority)
                continue;
            if (!this.bodies.TryGetBodyGeneration(identity, out ulong bodyGeneration)
                || !this.TryGetBoundBody(identity, bodyGeneration, out _))
                continue;
            this.Deliver(identity, runtime, pending, bodyGeneration);
        }
    }

    public void Render(RenderedWorldEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.currentLocation is null)
            return;
        foreach ((CompanionIdentity identity, SpeechRuntime runtime) in this.runtimes)
        {
            if (runtime.Current is not ActiveSpeech speech
                || speech.ExpiresAtTick <= this.currentTick
                || !this.TryGetVisibleBody(identity, speech.BodyGeneration, out NPC body))
                continue;
            DrawBubble(e.SpriteBatch, body, speech.Text, BubbleAlpha(speech, this.currentTick));
        }
    }

    public void Clear()
    {
        this.runtimes.Clear();
        this.currentTick = 0;
        this.clientEpoch = string.Empty;
    }

    public void Suspend()
    {
        foreach (SpeechRuntime runtime in this.runtimes.Values)
        {
            runtime.Pending = null;
            runtime.Current = null;
        }
    }

    private void OfferFailure(CompanionIdentity identity, string sourceEventId, string code)
    {
        string category = FailureCategory(code);
        this.Offer(identity, $"speech.failure.{category}", $"failure.{category}", SpeechPriorities.Critical, FailureCooldownTicks, sourceEventId);
    }

    private void Offer(CompanionIdentity identity, string translationKey, string topicKey, int priority, int cooldownTicks, string sourceEventId)
    {
        if (!identity.IsCanonical || string.IsNullOrWhiteSpace(sourceEventId))
            return;
        SpeechRuntime runtime = this.GetRuntime(identity);
        string text = BoundText(this.TranslateVariant(translationKey, identity, topicKey, $"{sourceEventId}:{runtime.LastSequence}"));
        if (text.Length == 0)
            return;
        if (runtime.TopicCooldowns.TryGetValue(topicKey, out ulong topicUntil) && topicUntil > this.currentTick)
            return;
        if (runtime.TextCooldowns.TryGetValue(text, out ulong textUntil) && textUntil > this.currentTick)
            return;
        var candidate = new SpeechCandidate(
            translationKey,
            sourceEventId,
            topicKey,
            text,
            priority,
            cooldownTicks,
            this.currentTick + CandidateLifetimeTicks);
        if (runtime.Pending is null || runtime.Pending.Value.Priority <= priority)
            runtime.Pending = candidate;
    }

    private void Deliver(CompanionIdentity identity, SpeechRuntime runtime, SpeechCandidate candidate, ulong bodyGeneration)
    {
        ulong sequence = runtime.LastSequence < ulong.MaxValue ? runtime.LastSequence + 1 : ulong.MaxValue;
        if (sequence == 0)
            sequence = 1;
        runtime.LastSequence = sequence;
        runtime.Pending = null;
        runtime.TopicCooldowns[candidate.TopicKey] = this.currentTick + (ulong)candidate.CooldownTicks;
        runtime.TextCooldowns[candidate.Text] = this.currentTick + TextRepeatCooldownTicks;
        var speech = new ActiveSpeech(
            sequence,
            bodyGeneration,
            candidate.SpeechId,
            candidate.TopicKey,
            candidate.Text,
            candidate.Priority,
            this.currentTick,
            this.currentTick + DisplayTicks);
        runtime.Current = speech;
        try
        {
            this.multiplayer.BroadcastSpeech(new SpeechEventDto
            {
                EventId = Guid.NewGuid().ToString("N"),
                SpeechSequence = sequence,
                OwnerId = identity.OwnerId,
                Slot = identity.Slot,
                BodyGeneration = bodyGeneration,
                SpeechId = candidate.SpeechId,
                TopicKey = candidate.TopicKey,
                Text = candidate.Text,
                Priority = candidate.Priority,
                StartedAtHostTick = this.currentTick,
                ExpiresAtHostTick = this.currentTick + DisplayTicks,
            });
        }
        catch (Exception ex)
        {
            this.monitor.Log($"HY-SPEECH-BROADCAST-FAILED: {identity} {ex.GetType().Name}.", LogLevel.Warn);
        }
    }

    private void AcceptRemote(CompanionIdentity identity, ulong sequence, ulong bodyGeneration, string speechId, string topicKey, string text, int priority, int remainingTicks)
    {
        SpeechRuntime runtime = this.GetRuntime(identity);
        if (sequence <= runtime.LastSequence)
            return;
        runtime.LastSequence = sequence;
        runtime.Pending = null;
        runtime.Current = new ActiveSpeech(
            sequence,
            bodyGeneration,
            speechId,
            topicKey,
            BoundText(text),
            priority,
            this.currentTick,
            this.currentTick + (ulong)Math.Clamp(remainingTicks, 1, SpeechEventContracts.MaximumLifetimeTicks));
    }

    private void ObserveVitalChanges()
    {
        foreach (CompanionRecord record in this.registry.Active)
        {
            SpeechRuntime runtime = this.GetRuntime(record.Identity);
            string next = record.Vitals.State;
            string previous = runtime.LastVitalState;
            runtime.LastVitalState = next;
            if (previous.Length > 0 && previous != next)
            {
                string? key = next switch
                {
                    CompanionVitalStates.Retreating => "speech.vitals.retreating",
                    CompanionVitalStates.Downed => "speech.vitals.downed",
                    CompanionVitalStates.Resting => "speech.vitals.resting",
                    CompanionVitalStates.Recovering => "speech.vitals.recovering",
                    CompanionVitalStates.Active when previous != CompanionVitalStates.Active => "speech.vitals.recovered",
                    _ => null,
                };
                if (key is not null)
                    this.Offer(record.Identity, key, $"vitals.{next}", next is CompanionVitalStates.Retreating or CompanionVitalStates.Downed or CompanionVitalStates.Recovering ? SpeechPriorities.Critical : SpeechPriorities.Important, FailureCooldownTicks, $"vitals:{previous}:{next}:{this.currentTick}");
            }

            string fatigue = CompanionFatigueLevels.From(record.Vitals);
            string previousFatigue = runtime.LastFatigueLevel;
            runtime.LastFatigueLevel = fatigue;
            if (next != CompanionVitalStates.Active
                || previousFatigue.Length == 0
                || CompanionFatigueLevels.Severity(fatigue) <= CompanionFatigueLevels.Severity(previousFatigue))
                continue;
            string? fatigueKey = fatigue switch
            {
                CompanionFatigueLevels.Tired => "speech.vitals.tired",
                CompanionFatigueLevels.Critical => "speech.vitals.critical",
                _ => null,
            };
            if (fatigueKey is not null)
                this.Offer(record.Identity, fatigueKey, $"vitals.fatigue.{fatigue}", fatigue == CompanionFatigueLevels.Critical ? SpeechPriorities.Critical : SpeechPriorities.Important, FailureCooldownTicks, $"fatigue:{previousFatigue}:{fatigue}:{this.currentTick}");
        }
    }

    private void ObserveAmbientSpeech()
    {
        if (!Context.IsWorldReady)
            return;
        foreach (CompanionRecord record in this.registry.Active)
        {
            SpeechRuntime runtime = this.GetRuntime(record.Identity);
            if (runtime.NextAmbientTick == 0)
            {
                runtime.NextAmbientTick = this.currentTick + AmbientInitialDelayTicks
                    + StableVariance(record.Identity, "ambient-initial", AmbientInitialVarianceTicks);
                continue;
            }
            if (this.currentTick < runtime.NextAmbientTick)
                continue;
            runtime.NextAmbientTick = this.currentTick + AmbientIntervalTicks
                + StableVariance(record.Identity, $"ambient:{this.currentTick}", AmbientIntervalVarianceTicks);
            Farmer? owner = Game1.GetPlayer(record.OwnerId, onlyOnline: true);
            if (!record.WantsBody
                || !OwnerLifecycleGate.CanAdvance(owner)
                || record.Vitals.State != CompanionVitalStates.Active
                || record.Mode == CompanionModes.Work
                || !string.IsNullOrWhiteSpace(record.ActiveTransactionId)
                || owner is null
                || !this.bodies.TryGetBodyGeneration(record.Identity, out ulong bodyGeneration)
                || !this.TryGetBoundBody(record.Identity, bodyGeneration, out NPC body)
                || !ReferenceEquals(owner.currentLocation, body.currentLocation)
                || Math.Max(Math.Abs(owner.TilePoint.X - body.TilePoint.X), Math.Abs(owner.TilePoint.Y - body.TilePoint.Y)) > AmbientMaximumTileDistance)
                continue;
            string key = Game1.isRaining
                ? "speech.ambient.rain"
                : Game1.timeOfDay < 900
                    ? "speech.ambient.morning"
                    : Game1.timeOfDay >= 2100
                        ? "speech.ambient.evening"
                        : record.Mode == CompanionModes.Follow
                            ? "speech.ambient.follow"
                            : record.Mode == CompanionModes.Wait
                                ? "speech.ambient.wait"
                                : "speech.ambient.idle";
            this.Offer(record.Identity, key, key, SpeechPriorities.Normal, AmbientTopicCooldownTicks, $"ambient:{this.currentTick}");
        }
    }

    private void EnsureClientEpoch()
    {
        string epoch = this.multiplayer.SessionEpoch;
        if (epoch == this.clientEpoch)
            return;
        this.runtimes.Clear();
        this.clientEpoch = epoch;
    }

    private bool TryGetVisibleBody(CompanionIdentity identity, ulong generation, out NPC body)
    {
        if (this.bodies.TryGetBody(identity, out body)
            && ReferenceEquals(body.currentLocation, Game1.currentLocation)
            && CompanionBodyBinder.TryReadIdentity(body, out _, out ulong boundGeneration)
            && boundGeneration == generation)
            return true;
        foreach (NPC candidate in Game1.currentLocation.characters)
        {
            if (CompanionBodyBinder.TryReadIdentity(candidate, out CompanionIdentity candidateIdentity, out ulong candidateGeneration)
                && candidateIdentity == identity
                && candidateGeneration == generation)
            {
                body = candidate;
                return true;
            }
        }
        body = null!;
        return false;
    }

    private bool TryGetBoundBody(CompanionIdentity identity, ulong generation, out NPC body)
    {
        if (this.bodies.TryGetBody(identity, out body)
            && body.currentLocation is not null
            && CompanionBodyBinder.TryReadIdentity(body, out CompanionIdentity boundIdentity, out ulong boundGeneration)
            && boundIdentity == identity
            && boundGeneration == generation)
            return true;
        body = null!;
        return false;
    }

    private string Translate(string key)
    {
        string text = this.translation.Get(key).ToString();
        return string.IsNullOrWhiteSpace(text) ? key : text;
    }

    private string TranslateVariant(string key, CompanionIdentity identity, string topicKey, string salt)
    {
        string[] variants = this.Translate(key)
            .Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (variants.Length == 0)
            return key;
        uint hash = StableHash($"{identity.OwnerId}:{identity.Slot}:{topicKey}:{salt}");
        return variants[hash % (uint)variants.Length];
    }

    private SpeechRuntime GetRuntime(CompanionIdentity identity)
    {
        if (!this.runtimes.TryGetValue(identity, out SpeechRuntime? runtime))
        {
            runtime = new SpeechRuntime();
            this.runtimes.Add(identity, runtime);
        }
        return runtime;
    }

    private static bool IsReadOnlyCommand(string command) => command.EndsWith("-status", StringComparison.Ordinal)
        || command.EndsWith("-options", StringComparison.Ordinal)
        || command.EndsWith("-preview", StringComparison.Ordinal)
        || command.EndsWith("-list", StringComparison.Ordinal)
        || command == "operation-status";

    private static bool IsSilentCancellation(string code)
    {
        string value = code.ToUpperInvariant();
        return value.Contains("CANCELLED-BY-SAVE", StringComparison.Ordinal)
            || value.Contains("CANCELLED-BY-TITLE", StringComparison.Ordinal)
            || value.Contains("CANCELLED-BY-LOAD", StringComparison.Ordinal)
            || value.Contains("RECALLED", StringComparison.Ordinal)
            || value.Contains("DAY-ENDING", StringComparison.Ordinal);
    }

    private static string FailureCategory(string code)
    {
        string value = code.ToUpperInvariant();
        if (value.Contains("TOOL", StringComparison.Ordinal) || value.Contains("WEAPON", StringComparison.Ordinal)) return "tool";
        if (value.Contains("PATH", StringComparison.Ordinal) || value.Contains("ROUTE", StringComparison.Ordinal) || value.Contains("UNREACHABLE", StringComparison.Ordinal) || value.Contains("APPROACH", StringComparison.Ordinal)) return "path";
        if (value.Contains("STAMINA", StringComparison.Ordinal) || value.Contains("EXHAUST", StringComparison.Ordinal) || value.Contains("VITAL", StringComparison.Ordinal)) return "stamina";
        if (value.Contains("CAPACITY", StringComparison.Ordinal) || value.Contains("INVENTORY", StringComparison.Ordinal) || value.Contains("BAG", StringComparison.Ordinal) || value.Contains("OUTPUT", StringComparison.Ordinal)) return "capacity";
        if (value.Contains("PERMISSION", StringComparison.Ordinal) || value.Contains("AUTHORIZ", StringComparison.Ordinal) || value.Contains("NOT-OWNER", StringComparison.Ordinal)) return "permission";
        if (value.Contains("BUSY", StringComparison.Ordinal) || value.Contains("ACTIVE", StringComparison.Ordinal)) return "busy";
        if (value.Contains("TARGET", StringComparison.Ordinal) || value.Contains("CHANGED", StringComparison.Ordinal) || value.Contains("NOT-FOUND", StringComparison.Ordinal)) return "target";
        if (value.Contains("LIFECYCLE", StringComparison.Ordinal) || value.Contains("SAVING", StringComparison.Ordinal)) return "lifecycle";
        return "generic";
    }

    private static string NormalizeTaskKind(string taskKind) => taskKind switch
    {
        "Watering" => "watering",
        "Chopping" => "chopping",
        "Mining" => "mining",
        "Harvesting" => "harvesting",
        "Foraging" => "foraging",
        "Mowing" => "mowing",
        "Digging" => "digging",
        "AnimalCare" => "animal-care",
        "Fishing" => "fishing",
        "Combat" or "CombatCounterStrike" or "CombatGuardExchange" or "CombatGuard" => "combat",
        "Delivery" => "delivery",
        "Planting" => "planting",
        _ => "generic",
    };

    private static IReadOnlyList<string> BuildInteractionTopics(CompanionMenuIdentitySnapshot identity)
    {
        if (!identity.OwnerOnline)
            return new[] { "speech.talk.owner-offline" };
        if (identity.VitalState == CompanionVitalStates.Downed)
            return new[] { "speech.talk.downed" };
        if (identity.VitalState is CompanionVitalStates.Resting or CompanionVitalStates.Recovering)
            return new[] { "speech.talk.recovering" };

        var topics = new List<string>(8);
        if (identity.Mode == CompanionModes.Work)
            topics.Add(WorkTalkKey(identity.WorkKind));
        if (Game1.isRaining)
            topics.Add("speech.talk.rain");
        topics.Add(Game1.currentSeason switch
        {
            "spring" => "speech.talk.season.spring",
            "summer" => "speech.talk.season.summer",
            "fall" => "speech.talk.season.fall",
            "winter" => "speech.talk.season.winter",
            _ => "speech.talk.idle",
        });
        topics.Add(LocationTalkKey(identity.LocationSummary));
        if (Game1.timeOfDay < 900)
            topics.Add("speech.talk.morning");
        else if (Game1.timeOfDay >= 2100)
            topics.Add("speech.talk.evening");
        topics.Add(identity.HeartLevel >= 8
            ? "speech.talk.bond.devoted"
            : identity.HeartLevel >= 4
                ? "speech.talk.bond.close"
                : "speech.talk.bond.new");
        topics.Add(identity.Mode == CompanionModes.Follow ? "speech.talk.follow" : "speech.talk.idle");
        return topics.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string WorkTalkKey(string kind) => kind switch
    {
        WorkKinds.Water => "speech.talk.work.watering",
        WorkKinds.Chop or WorkKinds.Mow => "speech.talk.work.clearing",
        WorkKinds.Mine => "speech.talk.work.mining",
        WorkKinds.Harvest or WorkKinds.Forage => "speech.talk.work.gathering",
        "Fish" => "speech.talk.work.fishing",
        "Plant" => "speech.talk.work.planting",
        "Fight" => "speech.talk.work.combat",
        _ => "speech.talk.work",
    };

    private static string LocationTalkKey(string location)
    {
        if (location.Contains("Mine", StringComparison.OrdinalIgnoreCase)
            || location.Contains("Skull", StringComparison.OrdinalIgnoreCase)
            || location.Contains("Volcano", StringComparison.OrdinalIgnoreCase))
            return "speech.talk.location.mine";
        if (location.Contains("FarmHouse", StringComparison.OrdinalIgnoreCase)
            || location.Contains("Cabin", StringComparison.OrdinalIgnoreCase))
            return "speech.talk.location.home";
        if (location.Contains("Farm", StringComparison.OrdinalIgnoreCase))
            return "speech.talk.location.farm";
        return "speech.talk.location.away";
    }

    private static string BoundText(string value)
    {
        char[] clean = value.Where(character => !char.IsControl(character)).Take(SpeechEventContracts.MaximumTextCharacters).ToArray();
        return new string(clean).Trim();
    }

    private static ulong StableVariance(CompanionIdentity identity, string salt, int maximumExclusive) =>
        maximumExclusive <= 0 ? 0UL : StableHash($"{identity.OwnerId}:{identity.Slot}:{salt}") % (uint)maximumExclusive;

    private static uint StableHash(string value)
    {
        uint hash = 2166136261;
        foreach (char character in value)
        {
            hash ^= character;
            hash *= 16777619;
        }
        return hash;
    }

    private static float BubbleAlpha(ActiveSpeech speech, ulong tick)
    {
        ulong elapsed = tick >= speech.StartedAtTick ? tick - speech.StartedAtTick : 0;
        ulong remaining = speech.ExpiresAtTick > tick ? speech.ExpiresAtTick - tick : 0;
        float fadeIn = Math.Clamp(elapsed / (float)BubbleFadeTicks, 0f, 1f);
        float fadeOut = Math.Clamp(remaining / (float)BubbleFadeTicks, 0f, 1f);
        return Math.Min(fadeIn, fadeOut);
    }

    private static void DrawBubble(SpriteBatch batch, NPC body, string text, float alpha)
    {
        const float scale = 0.68f;
        const int maximumWidth = 300;
        string wrapped = WrapText(text, Game1.smallFont, maximumWidth, scale);
        Vector2 measured = Game1.smallFont.MeasureString(wrapped) * scale;
        Vector2 anchor = Game1.GlobalToLocal(Game1.viewport, body.Position + new Vector2(Game1.tileSize / 2f, -96f));
        int width = Math.Max(32, (int)MathF.Ceiling(measured.X) + 14);
        int height = Math.Max(22, (int)MathF.Ceiling(measured.Y) + 10);
        var panel = new Rectangle((int)anchor.X - width / 2, (int)anchor.Y - height, width, height);
        batch.Draw(Game1.staminaRect, panel, Color.Black * (0.82f * alpha));
        batch.Draw(Game1.staminaRect, new Rectangle(panel.X, panel.Y, panel.Width, 2), Color.White * (0.72f * alpha));
        batch.Draw(Game1.staminaRect, new Rectangle(panel.X, panel.Bottom - 2, panel.Width, 2), Color.White * (0.72f * alpha));
        batch.Draw(Game1.staminaRect, new Rectangle(panel.X, panel.Y, 2, panel.Height), Color.White * (0.72f * alpha));
        batch.Draw(Game1.staminaRect, new Rectangle(panel.Right - 2, panel.Y, 2, panel.Height), Color.White * (0.72f * alpha));
        batch.Draw(Game1.staminaRect, new Rectangle((int)anchor.X - 3, panel.Bottom, 6, 6), Color.Black * (0.82f * alpha));
        batch.DrawString(Game1.smallFont, wrapped, new Vector2(panel.X + 7, panel.Y + 5), Color.White * alpha, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
    }

    private static string WrapText(string text, SpriteFont font, int maximumWidth, float scale)
    {
        var result = new StringBuilder();
        var line = new StringBuilder();
        foreach (char character in text)
        {
            string proposed = line.ToString() + character;
            if (line.Length > 0 && font.MeasureString(proposed).X * scale > maximumWidth)
            {
                if (result.Length > 0)
                    result.Append('\n');
                result.Append(line);
                line.Clear();
            }
            line.Append(character);
        }
        if (line.Length > 0)
        {
            if (result.Length > 0)
                result.Append('\n');
            result.Append(line);
        }
        return result.ToString();
    }

    private sealed class SpeechRuntime
    {
        public ulong LastSequence { get; set; }
        public ulong InteractionCount { get; set; }
        public ulong NextAmbientTick { get; set; }
        public string LastVitalState { get; set; } = string.Empty;
        public string LastFatigueLevel { get; set; } = string.Empty;
        public SpeechCandidate? Pending { get; set; }
        public ActiveSpeech? Current { get; set; }
        public Dictionary<string, ulong> TopicCooldowns { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ulong> TextCooldowns { get; } = new(StringComparer.Ordinal);
    }

    private readonly record struct SpeechCandidate(
        string SpeechId,
        string SourceEventId,
        string TopicKey,
        string Text,
        int Priority,
        int CooldownTicks,
        ulong ExpiresAtTick);

    private readonly record struct ActiveSpeech(
        ulong Sequence,
        ulong BodyGeneration,
        string SpeechId,
        string TopicKey,
        string Text,
        int Priority,
        ulong StartedAtTick,
        ulong ExpiresAtTick);
}
