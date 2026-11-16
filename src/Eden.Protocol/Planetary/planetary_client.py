"""Build 3662168: align colony fitting and factory batches with signed world policy."""


def install_planetary_tuning():
    import sys
    # Keep default logins lazy; also restore defaults after reconnecting from a
    # tuned world in the same process. The values come from the signed prelude.
    if (eden_planetary_maximum == 0 and all(float(value) == 1.0 for value in eden_planetary_policy)
            and not getattr(sys.modules.get('planetCommon'), '_eden_planetary', False)):
        return
    import planetCommon
    from decimal import Decimal, ROUND_FLOOR
    from eve.common.script.planet.entities.processPin import ProcessPin

    cpu, power = (float(value) for value in eden_planetary_policy[:2])
    output = Decimal(eden_planetary_policy[2])
    original_cpu = getattr(planetCommon.GetCPUOutput, '_eden_original', planetCommon.GetCPUOutput)
    original_power = getattr(planetCommon.GetPowerOutput, '_eden_original', planetCommon.GetPowerOutput)
    original_schematic = getattr(ProcessPin.SetSchematic, '_eden_original', ProcessPin.SetSchematic)

    def cpu_output(level):
        return original_cpu(level) * cpu

    def power_output(level):
        return original_power(level) * power

    def set_schematic(self, schematicID, schematic=None):
        result = original_schematic(self, schematicID, schematic)
        self.products = dict((type_id, max(1, int((Decimal(quantity) * output).to_integral_value(rounding=ROUND_FLOOR))))
                             for type_id, quantity in self.products.items() if quantity > 0)
        return result

    cpu_output._eden_original = original_cpu
    power_output._eden_original = original_power
    set_schematic._eden_original = original_schematic
    planetCommon.GetCPUOutput = cpu_output
    planetCommon.GetPowerOutput = power_output
    ProcessPin.SetSchematic = set_schematic
    planetCommon._eden_planetary = True

    from eve.client.script.environment.planet.clientColony import ClientColony
    from types import FunctionType
    from eve.common.lib import appConst

    original_validate = getattr(ClientColony.PostValidateCreatePin, '_eden_original', ClientColony.PostValidateCreatePin)
    original_validate = getattr(original_validate, 'im_func', original_validate)
    if eden_planetary_maximum == 0:
        ClientColony.PostValidateCreatePin = original_validate
        return

    maximum = int(eden_planetary_maximum)

    class SkillView:
        def __init__(self, service):
            self.service = service

        def GetEffectiveLevel(self, type_id):
            if type_id == appConst.typeInterplanetaryConsolidation:
                return maximum - 1
            if type_id == appConst.typeOmnipotent:
                return None
            return self.service.GetEffectiveLevel(type_id)

        def __getattr__(self, name):
            return getattr(self.service, name)

    class ServiceView:
        def GetService(self, name):
            service = sm.GetService(name)
            return SkillView(service) if name == 'skills' else service

        def __getattr__(self, name):
            return getattr(sm, name)

    # Scope the effective colony-count skill to this one native validation.
    # Its wallet and required-building-skill checks still execute unchanged;
    # no character skill data or shared service instance is modified.
    code = getattr(original_validate, 'func_code', None) or original_validate.__code__
    namespace = dict(getattr(original_validate, 'func_globals', None) or original_validate.__globals__)
    namespace['sm'] = ServiceView()
    defaults = getattr(original_validate, 'func_defaults', None) or getattr(original_validate, '__defaults__', None)
    closure = getattr(original_validate, 'func_closure', None) or getattr(original_validate, '__closure__', None)
    validate = FunctionType(code, namespace, original_validate.__name__, defaults, closure)
    validate._eden_original = original_validate
    ClientColony.PostValidateCreatePin = validate


install_planetary_tuning()
