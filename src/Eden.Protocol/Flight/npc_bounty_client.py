"""Use the world's gross NPC bounty for in-space bounty displays in build 3662168."""


def install_npc_bounty_display():
    import sys
    from npcs.client.components import entity_bounty

    original = entity_bounty.get_entity_bounty
    if getattr(original, '_eden_npc_bounty', False):
        return

    def get_entity_bounty(type_id, item_id):
        if item_id is not None:
            slim = sm.GetService('michelle').GetItem(item_id)
            if slim is not None and slim.typeID == type_id:
                bounty = getattr(slim, 'edenNpcBounty', None)
                if bounty is not None:
                    return bounty
        return original(type_id, item_id)

    get_entity_bounty._eden_npc_bounty = True
    entity_bounty.get_entity_bounty = get_entity_bounty
    # Update existing imports without loading the in-flight UI during login.
    for name in ('eve.client.script.ui.inflight.activeitem',
                 'eve.client.script.ui.inflight.selectedItemWnd',
                 'eve.client.script.ui.shared.info.infoContentBase',
                 'eve.client.script.ui.shared.info.infosvc'):
        module = sys.modules.get(name)
        if getattr(module, 'get_entity_bounty', None) is original:
            module.get_entity_bounty = get_entity_bounty


install_npc_bounty_display()
