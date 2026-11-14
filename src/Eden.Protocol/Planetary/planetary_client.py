"""Build 3662168: align colony fitting and factory batches with signed world policy."""


def install_planetary_tuning():
    import sys
    # Keep default logins lazy; also restore defaults after reconnecting from a
    # tuned world in the same process. The values come from the signed prelude.
    if (all(float(value) == 1.0 for value in eden_planetary_policy)
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


install_planetary_tuning()
