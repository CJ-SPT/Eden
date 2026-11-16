"""Build 3662168: expose Eden's authenticated saved clone grade to the native Omega license reader."""


def install_saved_clone_license():
    from datetime import datetime
    from user_licenses.client.messenger.request_messenger import PublicUserLicenseRequestMessenger
    from user_licenses.client.user_license import UserLicense
    from user_licenses.client.user_license_type import UserLicenseType

    current = PublicUserLicenseRequestMessenger.get_user_license_request
    if getattr(current, '_eden_saved_clone_grade', False):
        return
    original = getattr(current, 'im_func', current)
    omega_clone_grade = 1  # Eden.Gameplay.Docking.AccountCloneGrade.Omega.

    def get_user_license_request(self, license_type):
        if license_type != UserLicenseType.OMEGA:
            return original(self, license_type)
        # MachoNet authenticates this read against the connection's account. The
        # public gateway's client-supplied identity is not an account credential.
        grade = sm.RemoteSvc('subscriptionMgr').GetCloneGrade()
        if grade != omega_clone_grade:
            return None
        # A saved Omega grade is permanent. The pinned UserLicense represents
        # that with no expiry and accepts a stable epoch for last-modified metadata.
        return UserLicense(expiry_date=None, last_modified_date=datetime(1970, 1, 1))

    get_user_license_request._eden_saved_clone_grade = True
    PublicUserLicenseRequestMessenger.get_user_license_request = get_user_license_request


install_saved_clone_license()
