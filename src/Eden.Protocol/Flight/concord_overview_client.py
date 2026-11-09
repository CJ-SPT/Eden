"""Build 3662168: keep CONCORD's overview/bracket icon blue during aggression.

The native entity-standing color helper otherwise replaces a friendly icon with
red when threatAttackingMe is set. Target locks and threat state remain intact.
"""


def install_concord_overview():
    from inventorycommon import const
    from spacecomponents.client.components import entitystandings

    original = entitystandings._get_icon_color_and_hint_from_entity_standing_response
    if getattr(original, '_eden_concord_blue', False):
        return

    def concord_icon_color(slim_item, standings):
        if getattr(slim_item, 'groupID', None) == const.groupConcordDrone:
            return entitystandings.ICON_SORT_ORDER_HINT_BY_STANDINGS_CLASS[
                entitystandings.STANDINGS_FRIENDLY]
        return original(slim_item, standings)

    concord_icon_color._eden_concord_blue = True
    entitystandings._get_icon_color_and_hint_from_entity_standing_response = concord_icon_color


install_concord_overview()
