"""Build 3662168: align colony counts, fitting, factory batches and extraction previews with world policy."""


def install_planetary_tuning():
    import sys
    # Keep default logins lazy; also restore defaults after reconnecting from a
    # tuned world in the same process. The values come from the signed prelude.
    if (eden_planetary_maximum == 0 and all(float(value) == 1.0 for value in eden_planetary_policy)
            and not getattr(sys.modules.get('eve.common.script.util.planetCommon'), '_eden_planetary', False)):
        return
    from eve.common.script.util import planetCommon
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

    from eve.client.script.ui.shared.planet.pinContainers.ProcessorContainer import ProcessorContainer
    import localization

    original_update = getattr(ProcessorContainer._UpdateInfoCont, '_eden_original', ProcessorContainer._UpdateInfoCont)
    original_update = getattr(original_update, 'im_func', original_update)

    def update_info(self):
        result = original_update(self)
        if self.pin.schematicID:
            # The native panel reads nominal quantity from shared schematic data,
            # but its volume/routing displays already use this pin's scaled products.
            amount = sum(self.pin.GetProducts().values())
            self.amountPerCycleTxt.SetSubtext(localization.GetByLabel('UI/PI/Common/UnitsAmount', amount=amount))
        return result

    update_info._eden_original = original_update
    ProcessorContainer._UpdateInfoCont = original_update if output == 1 else update_info

    from eve.client.script.environment.planet.clientColony import ClientColony
    from eve.client.script.ui.shared.planet.planetEditModeContainer import PlanetEditModeContainer
    from eve.client.script.ui.shared.planet.planetsWindow.coloniesPanel import ColoniesPanel
    from types import FunctionType
    from eve.common.lib import appConst

    maximum = int(eden_planetary_maximum)
    original_program = getattr(ClientColony.CreateProgram, '_eden_original', ClientColony.CreateProgram)
    original_program = getattr(original_program, 'im_func', original_program)

    def create_program(self, harmonic, ecuPinID, resourceTypeID, points=None, headRadius=None):
        result = original_program(self, harmonic, ecuPinID, resourceTypeID, points, headRadius)
        pin = self.GetPin(ecuPinID)
        heads = list(pin.heads if points is None else points)
        if not heads or resourceTypeID is None:
            return result
        radius = pin.GetExtractorHeadRadius() if headRadius is None else headRadius
        # SurveyWindow otherwise uses BaseColony's retail-only estimate. Reuse the
        # same authoritative quote as InstallProgram, including temporary ECU IDs,
        # edited heads and the active extraction multiplier. Preserve native overlap hints.
        quantity, cycle, count = self.planet.remoteHandler.GetProgramResultInfo(ecuPinID, resourceTypeID, heads, radius)
        return quantity, cycle, count, result[3]

    create_program._eden_original = original_program
    tuned_policy = maximum != 0 or any(float(value) != 1.0 for value in eden_planetary_policy)
    ClientColony.CreateProgram = create_program if tuned_policy else original_program

    class SkillView:
        def __init__(self, service):
            self.service = service

        def GetEffectiveLevel(self, type_id):
            if type_id == appConst.typeInterplanetaryConsolidation:
                return maximum - 1
            if type_id == appConst.typeOmnipotent:
                return None
            return self.service.GetEffectiveLevel(type_id)

        def GetMyLevel(self, type_id):
            if type_id == appConst.typeInterplanetaryConsolidation:
                return maximum - 1
            return self.service.GetMyLevel(type_id)

        def __getattr__(self, name):
            return getattr(self.service, name)

    class ServiceView:
        def GetService(self, name):
            service = sm.GetService(name)
            return SkillView(service) if name == 'skills' else service

        def __getattr__(self, name):
            return getattr(sm, name)

    def tune_method(owner, name, overrides):
        original = getattr(getattr(owner, name), '_eden_original', getattr(owner, name))
        original = getattr(original, 'im_func', original)
        if maximum == 0:
            setattr(owner, name, original)
            return
        code = getattr(original, 'func_code', None) or original.__code__
        namespace = dict(getattr(original, 'func_globals', None) or original.__globals__)
        namespace.update(overrides)
        defaults = getattr(original, 'func_defaults', None) or getattr(original, '__defaults__', None)
        closure = getattr(original, 'func_closure', None) or getattr(original, '__closure__', None)
        tuned = FunctionType(code, namespace, original.__name__, defaults, closure)
        tuned._eden_original = original
        setattr(owner, name, tuned)

    # Native Submit validation, the build-button gate and the colony directory
    # each read the count separately. Give only these functions a private view;
    # shared skills, cargo/location checks and owned-colony rows stay unchanged.
    service_view = ServiceView()
    tune_method(ClientColony, 'PostValidateCreatePin', {'sm': service_view})
    tune_method(PlanetEditModeContainer, 'GetBuildEntryDisabledHint', {'sm': service_view})
    tune_method(ColoniesPanel, 'ConstructEmptyEntries', {'sm': service_view, 'MAX_NUM_COLONIES': maximum})


def record_planetary_failure():
    # GPS captures signed-expression exceptions in its login response. Preserve
    # this adapter's failure in the existing bounded local client-error log too.
    import sys
    error = sys.exc_info()
    try:
        import eden_skill_overlay
        eden_skill_overlay.record_client_error('planetary-tuning', eden_planetary_maximum, error)
    except Exception:
        pass


try:
    install_planetary_tuning()
except Exception:
    record_planetary_failure()
    raise
