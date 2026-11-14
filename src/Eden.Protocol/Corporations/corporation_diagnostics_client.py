"""Build 3662168: retain failures that leave corporation construction incomplete."""


def install_corporation_diagnostics():
    import os
    import sys
    import time
    from eve.client.script.ui.shared.neocom.corporation.corporationWindow import CorporationWindow
    from eve.client.script.ui.services.corporation.base_corporation import Corporation

    if getattr(CorporationWindow.ApplyAttributes, '_eden_corporation_diagnostic', False):
        return

    counts = [0, 0]

    def record(event, failed=False):
        # Separate quotas keep repeated successful opens from hiding failures.
        # Local only: no arguments, frame locals, character data or credentials.
        try:
            bucket = int(failed)
            if counts[bucket] >= 128:
                return
            counts[bucket] += 1
            frames = []
            error = None
            if failed:
                error_type, _, trace = sys.exc_info()
                error = error_type.__name__
                while trace is not None and len(frames) < 32:
                    code = trace.tb_frame.f_code
                    frames.append((code.co_filename, code.co_name, trace.tb_lineno))
                    trace = trace.tb_next
            root = os.environ.get('TEMP') or os.environ.get('TMP')
            if root:
                path = os.path.join(root, 'eden-corporation-%s.log' % os.getpid())
                with open(path, 'w' if sum(counts) == 1 else 'a') as output:
                    output.write('time=%s event=%s error=%s frames=%r\n' % (time.time(), event, error, frames))
        except Exception:
            # An unavailable diagnostic file must not alter native behavior.
            pass

    def observe(cls, name):
        original = getattr(cls, name)

        def observed(self, *args, **kwargs):
            record(name + ':begin')
            try:
                result = original(self, *args, **kwargs)
            except Exception:
                record(name + ':failed', failed=True)
                raise
            record(name + ':complete')
            return result

        observed._eden_corporation_diagnostic = True
        setattr(cls, name, observed)

    for name in ('ApplyAttributes', 'construct_corporation_panels', 'construct_administration_panels',
                 'construct_alliance_panels', 'reconstruct_main_tabs', 'SelectPanel'):
        observe(CorporationWindow, name)
    observe(Corporation, 'InviteToJoinCorp')
    record('installed-v1')


install_corporation_diagnostics()
