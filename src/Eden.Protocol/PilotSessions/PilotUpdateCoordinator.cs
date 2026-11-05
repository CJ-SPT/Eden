using Eden.Gameplay.Docking;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Calendar;
using Eden.Protocol.Characters;
using Eden.Protocol.Chat;
using Eden.Protocol.Combat;
using Eden.Protocol.Contracts;
using Eden.Protocol.Corporations;
using Eden.Protocol.Docking;
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
    CorporationHandler corporations
)
{
    public bool IsAccountMonitoring => redemption.IsMonitoring;
    public bool IsMonitoring =>
        corporations.IsMonitoring
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
        || boosters.IsMonitoring;

    public void Enter(PilotCallContext context)
    {
        corporations.Enter(context);
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
        repairs.Enter(context);
        npcCombat.Enter(context);
    }

    public void Leave(PilotCallContext context)
    {
        corporations.Reset();
        chat.Dispose();
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
        context.Flight.Checkpoint();
        mining.Cancel(context);
        combat.Cancel(context);
        missiles.Cancel(context);
        shields.Cancel(context);
        repairs.Cancel(context);
        hardeners.Cancel(context);
        capacitorBoosters.Cancel(context);
        support.Cancel(context);
        tackle.Leave(context);
        propulsion.Cancel(context);
        targeting.Reset();
        npcCombat.Leave();
        industry.Reset();
        refits.Clear();
    }

    public DockedPilot? Advance(PilotCallContext context)
    {
        redemption.Advance(context);
        using (SessionPerformance.Phase("update.targeting"))
        {
            targeting.Advance(context);
        }
        capacitorBoosters.Advance(context);
        hardeners.Advance(context);
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
        shields.Advance(context);
        using (SessionPerformance.Phase("update.repairs"))
        {
            repairs.Advance(context);
        }
        propulsion.Advance(context);
        industry.Advance(context);
        DockedPilot? beforeAttack = context.ActiveFlight?.Pilot;
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
            walletBalance.Advance(context);
        }
        return beforeAttack;
    }
}
