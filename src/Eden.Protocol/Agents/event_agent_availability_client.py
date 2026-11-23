"""Use Eden's Event mission eligibility in build 3662168's station Agents panel."""


def install_event_agent_availability():
    from eve.client.script.ui.shared.dockedUI import agents
    from eve.common.lib.appConst import agentTypeEventMissionAgent

    original = agents.find_available_and_unavailable_agents
    if getattr(original, '_eden_event_agent_availability', False):
        return

    def classify(agents_in_station, local_relevant_agents, relevant_agents):
        available, unavailable = original(agents_in_station, local_relevant_agents, relevant_agents)
        listed = set(available).union(unavailable)
        event_ids = [agent.agentID for agent in agents_in_station
                     if agent.agentTypeID == agentTypeEventMissionAgent and agent.agentID in listed]
        if event_ids:
            available_events = set(sm.RemoteSvc('agentMgr').GetAvailableEventAgents(event_ids))
            events = set(event_ids)
            available = [agent_id for agent_id in available if agent_id not in events]
            unavailable = [agent_id for agent_id in unavailable if agent_id not in events]
            available.extend(agent_id for agent_id in event_ids if agent_id in available_events)
            unavailable.extend(agent_id for agent_id in event_ids if agent_id not in available_events)
        return available, unavailable

    classify._eden_event_agent_availability = True
    agents.find_available_and_unavailable_agents = classify


install_event_agent_availability()
