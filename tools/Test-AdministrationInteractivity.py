"""Automated browser regression against an isolated administration test host.

Install: python -m pip install playwright; python -m playwright install firefox chromium
Run via AdministrationTests.LiveSettingsStayInteractiveInFirefoxAndChromium with
EDEN_TEST_ADMIN_BROWSER=1. Only the disposable test world's credentials are used.
"""

import sys

from playwright.sync_api import expect, sync_playwright


def main():
    address, browser_name, culture, separator = sys.argv[1:]
    with sync_playwright() as playwright:
        browser = getattr(playwright, browser_name).launch(headless=True)
        page = browser.new_page(locale=culture)
        page.set_default_timeout(10000)
        page.on("console", lambda message: print(message.type, message.text))
        page.on("pageerror", lambda error: print("PAGE ERROR", error))
        page.goto(address)
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
        expect(page.get_by_text("Unsaved changes", exact=True)).to_be_visible()
        expect(page.locator("#blazor-error-ui")).to_be_hidden()
        edit("Align speed (×)", "1.25")
        edit("Autopilot gate landing distance (metres)", "0")
        distance = page.get_by_label("Autopilot gate landing distance (metres)", exact=True)
        explanation = page.locator("#" + distance.get_attribute("aria-describedby"))
        expect(explanation).not_to_be_visible()
        help_button = page.locator('summary[aria-label="Help for Autopilot gate landing distance (metres)"]')
        help_button.focus()
        help_button.press("Enter")
        expect(explanation).to_be_visible()
        expect(explanation).to_contain_text("15,000 metres")
        help_button.press("Enter")
        expect(explanation).not_to_be_visible()
        check_tabs()
        click("Travel")
        expect(page.get_by_label("Warp speed (×)", exact=True)).to_have_value("2")
        expect(page.get_by_label("Align speed (×)", exact=True)).to_have_value("1.25")
        expect(page.get_by_label("Autopilot gate landing distance (metres)", exact=True)).to_have_value("0")
        click("Gathering")
        edit("Ore mining yield (×)", "0.05")
        expect(page.locator(".validation-message").first).to_be_visible()
        edit("Ore mining yield (×)", "2")
        click("Save for restart")
        expect(page.get_by_text("Revision 2 saved · pending restart.", exact=True)).to_be_visible()
        expect(page.get_by_role("button", name="Save for restart", exact=True)).to_be_disabled()
        click("Economy & market")
        page.get_by_label("Market preset", exact=True).select_option("Custom")
        for label in ("Empire LP-store datacores", "Simulated market history", "Regional hub supply",
                      "Beginner market supply"):
            page.get_by_label(label, exact=True).uncheck()
        click("Player bots")
        page.get_by_label("Enable player bots", exact=True).uncheck()
        page.get_by_label("Simulated miners", exact=True).uncheck()
        click("Progression")
        page.get_by_label("Grant civilian starting equipment to new pilots", exact=True).uncheck()
        click("Player bots")
        edit("Station traffic density (×)", "0.5")
        click("Rewards")
        edit("NPC bounty ISK (×)", "3")
        click("Save for restart")
        expect(page.get_by_text("Revision 3 saved · pending restart.", exact=True)).to_be_visible()
        check_tabs()
        click("Player bots")
        expect(page.get_by_label("Enable player bots", exact=True)).not_to_be_checked()
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
        click("Economy & market")
        page.get_by_label("Market preset", exact=True).select_option("Custom")
        for label in ("Empire LP-store datacores", "Simulated market history", "Regional hub supply",
                      "Beginner market supply"):
            expect(page.get_by_label(label, exact=True)).not_to_be_checked()
        click("Progression")
        expect(page.get_by_label("Grant civilian starting equipment to new pilots", exact=True)).not_to_be_checked()
        page.set_viewport_size({"width": 390, "height": 844})
        click("Player bots")
        assert page.evaluate("document.documentElement.scrollWidth === innerWidth"), "Settings overflow the mobile viewport"
        expect(page.get_by_role("heading", name="Hauler freight")).to_be_visible()
        expect(page.locator("#blazor-error-ui")).to_be_hidden()
        page.get_by_role("link", name="Performance", exact=True).click()
        expect(page.get_by_role("heading", name="Session frame latency")).to_be_visible()
        expect(page.get_by_role("heading", name="Frame processing time")).to_be_visible()
        expect(page.get_by_role("heading", name="Frame start delay")).to_be_visible()
        expect(page.get_by_text(f"8{separator}00 ms", exact=True)).to_be_visible()
        expect(page.get_by_text(f"12{separator}00 ms", exact=True)).to_be_visible()
        expect(page.locator(".frame-readings").get_by_text(f"3{separator}00 ms", exact=True)).to_be_visible()
        bars = page.locator(".frame-chart-bar").all()
        assert len(bars) >= 2
        for bar in bars:
            assert bar.bounding_box()["height"] > 1, "Frame graph bar did not scale with its measurements"
        assert page.evaluate("document.documentElement.scrollWidth === innerWidth"), "Performance overflows the mobile viewport"
        sampled = page.get_by_text("Sampled ", exact=False)
        first_sample = sampled.inner_text()
        expect(sampled).not_to_have_text(first_sample)
        assert not errors, errors
        browser.close()


if __name__ == "__main__":
    main()
