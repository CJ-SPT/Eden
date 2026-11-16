"""Automated browser regression against an isolated administration test host.

Install: python -m pip install playwright; python -m playwright install firefox chromium
Run via AdministrationTests.LiveSettingsStayInteractiveInFirefoxAndChromium with
EDEN_TEST_ADMIN_BROWSER=1. Only the disposable test world's credentials are used.
"""

import sys

from playwright.sync_api import expect, sync_playwright


def main():
    with sync_playwright() as playwright:
        browser = getattr(playwright, sys.argv[2]).launch(headless=True)
        page = browser.new_page()
        page.set_default_timeout(10000)
        page.on("console", lambda message: print(message.type, message.text))
        page.on("pageerror", lambda error: print("PAGE ERROR", error))
        page.goto(sys.argv[1])
        page.get_by_label("Administrator name").fill("Corey")
        page.get_by_label("Password").fill("local administrator password")
        page.get_by_role("button", name="Sign in", exact=True).click()

        # Prerendered controls precede the interactive connection. Wait for a tab
        # event to take effect before editing, rather than sleeping for hydration.
        rewards = page.get_by_role("button", name="Rewards", exact=True)
        for attempt in range(20):
            rewards.click()
            try:
                expect(rewards).to_have_attribute("aria-pressed", "true", timeout=500)
                break
            except AssertionError:
                if attempt == 19:
                    raise

        errors = []
        page.on("pageerror", lambda error: errors.append(str(error)))

        def click(name):
            page.get_by_role("button", name=name, exact=True).click()

        def edit(label, value):
            control = page.get_by_label(label, exact=True)
            control.fill(value)
            control.blur()

        def check_tabs():
            tabs = page.get_by_role("navigation", name="World settings categories")
            for tab in tabs.get_by_role("button").all():
                tab.click()
                expect(tab).to_have_attribute("aria-pressed", "true")
                expect(tabs.locator('[aria-pressed="true"]')).to_have_count(1)
                expect(page.locator("#" + tab.get_attribute("aria-controls"))).to_be_visible()

        check_tabs()
        click("Travel")
        edit("Warp speed (×)", "2")
        edit("Align speed (×)", "1.25")
        edit("Autopilot gate landing distance (metres)", "0")
        check_tabs()
        click("Travel")
        expect(page.get_by_label("Warp speed (×)", exact=True)).to_have_value("2")
        expect(page.get_by_label("Align speed (×)", exact=True)).to_have_value("1.25")
        expect(page.get_by_label("Autopilot gate landing distance (metres)", exact=True)).to_have_value("0")
        click("Gathering")
        edit("Ore mining yield (×)", "2")
        click("Save for restart")
        expect(page.get_by_text("Revision 2 saved · pending restart.", exact=True)).to_be_visible()
        expect(page.get_by_role("button", name="Save for restart", exact=True)).to_be_disabled()
        click("World & economy")
        for label in ("Empire LP-store datacores", "Simulated market history", "Regional hub supply",
                      "Beginner market supply", "Enable player bots"):
            page.get_by_label(label, exact=True).uncheck()
        page.get_by_label("Simulated miners", exact=True).uncheck()
        click("Progression")
        page.get_by_label("Grant civilian starting equipment to new pilots", exact=True).uncheck()
        click("World & economy")
        edit("Station traffic density (×)", "0.5")
        click("Rewards")
        edit("NPC bounty ISK (×)", "3")
        click("Save for restart")
        expect(page.get_by_text("Revision 3 saved · pending restart.", exact=True)).to_be_visible()
        check_tabs()
        click("World & economy")
        expect(page.get_by_label("Simulated miners", exact=True)).not_to_be_checked()
        expect(page.get_by_label("Station traffic density (×)", exact=True)).to_have_value("0.5")
        click("Gathering")
        edit("Ore mining yield (×)", "")
        expect(page.locator(".validation-message").first).to_be_visible()
        click("Reset Ore mining yield to 1×")
        expect(page.get_by_label("Ore mining yield (×)", exact=True)).to_have_value("1")
        expect(page.locator(".validation-message")).to_have_count(0)
        click("Discard unsaved changes")
        expect(page.get_by_label("Ore mining yield (×)", exact=True)).to_have_value("2")
        click("Validate draft")
        expect(page.get_by_text("Draft validation passed.", exact=True)).to_be_visible()
        click("Reset draft to original")
        expect(page.get_by_label("Ore mining yield (×)", exact=True)).to_have_value("1")
        click("Reload saved values")
        expect(page.get_by_label("Ore mining yield (×)", exact=True)).to_have_value("2")
        expect(page.get_by_text("Saved · pending restart", exact=True)).to_be_visible()
        click("World & economy")
        for label in ("Empire LP-store datacores", "Simulated market history", "Regional hub supply",
                      "Beginner market supply", "Enable player bots"):
            expect(page.get_by_label(label, exact=True)).not_to_be_checked()
        click("Progression")
        expect(page.get_by_label("Grant civilian starting equipment to new pilots", exact=True)).not_to_be_checked()
        assert not errors, errors
        browser.close()


if __name__ == "__main__":
    main()
