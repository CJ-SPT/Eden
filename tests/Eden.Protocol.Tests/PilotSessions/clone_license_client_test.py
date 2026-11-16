"""Headless license compatibility checks; no client UI or game session."""
import marshal
import builtins
from datetime import datetime
from pathlib import Path
import sys
from types import ModuleType, SimpleNamespace

expression = marshal.loads(Path(sys.argv[1]).read_bytes())
names = ['user_licenses', 'user_licenses.client', 'user_licenses.client.messenger',
         'user_licenses.client.messenger.request_messenger', 'user_licenses.client.user_license',
         'user_licenses.client.user_license_type']
for name in names:
    module = ModuleType(name)
    module.__path__ = []
    sys.modules[name] = module
    parent, _, child = name.rpartition('.')
    if parent:
        setattr(sys.modules[parent], child, module)


class Messenger:
    def get_user_license_request(self, license_type):
        raise RuntimeError('original request: ' + license_type)


class UserLicense:
    def __init__(self, expiry_date, last_modified_date):
        self.expiry_date = expiry_date
        self.last_modified_date = last_modified_date


sys.modules[names[3]].PublicUserLicenseRequestMessenger = Messenger
sys.modules[names[4]].UserLicense = UserLicense
sys.modules[names[5]].UserLicenseType = SimpleNamespace(OMEGA='omega')
grade = [0]
calls = []


def remote_service(name):
    assert name == 'subscriptionMgr'
    calls.append(name)
    return SimpleNamespace(GetCloneGrade=lambda: grade[0])


sm = SimpleNamespace(RemoteSvc=remote_service)
builtins.sm = sm
for _ in range(2):
    assert eval(expression, {'sm': sm}) == {}
    assert calls == []
messenger = Messenger()
for saved_grade in [0, 1, 0, 1, 2]:
    grade[0] = saved_grade
    result = messenger.get_user_license_request('omega')
    if saved_grade == 1:
        assert isinstance(result, UserLicense)
        assert result.expiry_date is None
        assert result.last_modified_date == datetime(1970, 1, 1)
    else:
        assert result is None
assert len(calls) == 5
try:
    messenger.get_user_license_request('mct')
    raise AssertionError('unsupported licenses must use their original handler')
except RuntimeError as error:
    assert str(error) == 'original request: mct'
assert len(calls) == 5
print('Saved clone license assertions passed')
