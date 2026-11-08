using Eden.Gameplay.Docking;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Calendar;
using Eden.Protocol.Characters;
using Eden.Protocol.Chat;
using Eden.Protocol.Combat;
using Eden.Protocol.Contracts;
using Eden.Protocol.Corporations;
using Eden.Protocol.Defenses;
using Eden.Protocol.Docking;
using Eden.Protocol.Drones;
using Eden.Protocol.Fleets;
using Eden.Protocol.Industry;
using Eden.Protocol.Mail;
using Eden.Protocol.Market;
using Eden.Protocol.Mining;
using Eden.Protocol.Notifications;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Redemption;
using Eden.Protocol.Repair;
using Eden.Protocol.Skills;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.PilotSessions;

[Injectable(InjectionType.Scoped)]
internal sealed class PilotUpdateCoordinator(
    TargetingHandler targeting,
    MiningHandler mining,
    CombatHandler combat,
    ShieldBoostHandler shields,
    PropulsionHandler propulsion,
    IndustryHandler industry,
    NpcCombatHandler npcCombat,
    LocalRepairHandler repairs,
    HardenerHandler hardeners,
    CapacitorBoosterHandler capacitorBoosters,
    MissileHandler missiles,
    SupportModuleHandler support,
    TackleHandler tackle,
    PlayerEwarHandler electronicWarfare,
    DeferredRefitPrimes refits,
    StationGuestHandler guests,
    OnlineStatusHandler online,
    CalendarHandler calendar,
    InboxHandler inbox,
    MailHandler mail,
    MailingListHandler mailingLists,
    ContractHandler contracts,
    SecurityStatusHandler security,
    RedemptionHandler redemption,
    FreeSkillPointHandler freePoints,
    SkillInjectorHandler injectors,
    SkillExtractionHandler extraction,
    LoyaltyWalletHandler loyalty,
    ImplantHandler implants,
    BoosterHandler boosters,
    WalletBalanceMonitor walletBalance,
    ChatHandler chat,
    CorporationHandler corporations,
    CorporationWalletHandler corporationWallet,
    NpcEwarHandler npcEwar,
    DroneHandler drones,
    PersonalMarketMonitor personalMarket,
    CrimewatchHandler crimewatch,
    PilotDamageMonitor playerDamage,
    CooperativeFleetHandler fleets,
    RemoteRepairHandler remoteRepairs,
    CapacitorWarfareHandler capacitorWarfare,
    RemoteAssistanceHandler remoteAssistance,
    SignatureSuppressionHandler signatureSuppression,
    AssaultDamageControlHandler assaultDamageControl
)
{
    public bool IsAccountMonitoring => redemption.IsMonitoring;
    public bool IsMonitoring =>
        corporations.IsMonitoring
        || fleets.IsMonitoring
        || industry.IsMonitoring
        || guests.IsDocked
        || calendar.IsMonitoring
        || inbox.IsMonitoring
        || mail.IsMonitoring
        || mailingLists.IsMonitoring
        || contracts.IsMonitoring
        || security.IsMonitoring
        || freePoints.IsMonitoring
        || injectors.IsMonitoring
        || extraction.IsMonitoring
        || loyalty.IsMonitoring
        || implants.IsMonitoring
        || boosters.IsMonitoring
        || crimewatch.IsMonitoring;

    public void Enter(PilotCallContext context)
    {
        playerDamage.Enter(context);
        remoteRepairs.Enter(context);
        capacitorWarfare.Enter(context);
        crimewatch.Enter(context);
        corporations.Enter(context);
        corporationWallet.Enter(context);
        fleets.Advance(context);
        chat.Advance(context);
        guests.Advance(context);
        calendar.Enter(context);
        inbox.Enter(context);
        mailingLists.Enter(context);
        mail.Enter(context);
        contracts.Enter(context);
        security.Enter(context);
        freePoints.Enter(context);
        injectors.Enter(context);
        extraction.Enter(context);
        loyalty.Enter(context);
        implants.Enter(context);
        boosters.Enter(context);
        walletBalance.Enter(context);
        personalMarket.Enter(context);
        repairs.Enter(context);
        npcCombat.Enter(context);
    }

    public void Leave(PilotCallContext context)
    {
        ApplyPlayerDamage(context);
        context.ActiveFlight?.Checkpoint();
        context.ActiveFlight?.PrepareDisconnect();
        chat.Dispose();
        fleets.Dispose();
        PrepareTranslocation(context);
    }

    // Travel cancels old activities, but the selected pilot remains online.
    // Chat presence updates Local access after arrival without closing its transport.
    public void PrepareTranslocation(PilotCallContext context)
    {
        playerDamage.Dispose();
        crimewatch.Reset();
        corporations.Reset();
        corporationWallet.Reset();
        guests.Dispose();
        online.Reset();
        calendar.Reset();
        inbox.Reset();
        mailingLists.Reset();
        mail.Reset();
        contracts.Reset();
        security.Reset();
        freePoints.Reset();
        injectors.Reset();
        extraction.Reset();
        loyalty.Reset();
        implants.Reset();
        boosters.Reset();
        walletBalance.Reset();
        personalMarket.Reset();
        context.Flight.Checkpoint();
        mining.Cancel(context);
        combat.Cancel(context);
        missiles.Cancel(context);
        shields.Cancel(context);
        repairs.Cancel(context);
        remoteRepairs.Cancel(context);
        capacitorWarfare.Cancel(context);
        remoteAssistance.Cancel(context);
        hardeners.Cancel(context);
        signatureSuppression.Cancel(context);
        assaultDamageControl.Cancel(context);
        capacitorBoosters.Cancel(context);
        support.Cancel(context);
        tackle.Leave(context);
        electronicWarfare.Cancel(context);
        npcEwar.Leave(context);
        propulsion.Cancel(context);
        targeting.Reset();
        npcCombat.Leave();
        industry.Reset();
        refits.Clear();
    }

    public DockedPilot? Advance(PilotCallContext context)
    {
        DockedPilot? beforeAttack = context.ActiveFlight?.Pilot;
        ApplyPlayerDamage(context);
        crimewatch.Advance(context);
        npcEwar.Advance(context);
        electronicWarfare.Advance(context);
        redemption.Advance(context);
        using (SessionPerformance.Phase("update.targeting"))
        {
            targeting.Advance(context);
        }
        remoteRepairs.Advance(context);
        capacitorWarfare.Advance(context);
        remoteAssistance.Advance(context);
        capacitorBoosters.Advance(context);
        drones.Advance(context);
        hardeners.Advance(context);
        signatureSuppression.Advance(context);
        assaultDamageControl.Advance(context);
        support.Advance(context);
        using (SessionPerformance.Phase("update.tackle"))
        {
            tackle.Advance(context);
        }
        mining.Advance(context);
        using (SessionPerformance.Phase("update.combat"))
        {
            combat.Advance(context);
        }
        using (SessionPerformance.Phase("update.missiles"))
        {
            missiles.Advance(context);
        }
        ApplyPlayerDamage(context);
        shields.Advance(context);
        using (SessionPerformance.Phase("update.repairs"))
        {
            repairs.Advance(context);
        }
        propulsion.Advance(context);
        industry.Advance(context);
        using (SessionPerformance.Phase("update.npcCombat"))
        {
            npcCombat.Advance(context);
        }
        using (SessionPerformance.Phase("update.targeting"))
        {
            targeting.Advance(context);
        }
        using (SessionPerformance.Phase("update.background"))
        {
            corporations.Advance(context);
            corporationWallet.Advance(context);
            fleets.Advance(context);
            chat.Advance(context);
            guests.Advance(context);
            online.Advance(context);
            calendar.Advance(context);
            inbox.Advance(context);
            mailingLists.Advance(context);
            mail.Advance(context);
            contracts.Advance(context);
            security.Advance(context);
            freePoints.Advance(context);
            injectors.Advance(context);
            extraction.Advance(context);
            loyalty.Advance(context);
            implants.Advance(context);
            boosters.Advance(context);
            personalMarket.Advance(context);
            walletBalance.Advance(context);
        }
        return beforeAttack;
    }

    public void ApplyPlayerDamage(PilotCallContext context)
    {
        long previous = context.ActiveFlight?.Pilot.ShipId ?? 0;
        playerDamage.Advance(context);
        if (context.Selected && context.Pilot.ShipId != previous)
        {
            // Clear both lock directions before the early capsule session delta
            // flushes the destroyed hull's removal.
            targeting.Advance(context);
        }
    }
}
