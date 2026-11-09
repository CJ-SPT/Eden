"""Check the rendered skill editor fixture with an automated headless browser.

Install once: python -m pip install playwright; python -m playwright install chromium
Export the fixture by setting EDEN_SKILL_LAYOUT_DIRECTORY before running
SkillBonusPresentationTests.LayoutFixtureRendersOneBonusInputAndThreeDistinctPrerequisiteRows.
"""
import argparse
from pathlib import Path
import re

from playwright.sync_api import sync_playwright


def check_layout(fixture_directory):
    root = Path(__file__).resolve().parent.parent
    app = root / "src/Eden.Server/Administration/Components/AdministrationApp.razor"
    stylesheet = re.search(r"<style>(.*?)</style>", app.read_text(encoding="utf-8"), re.S).group(1)
    stylesheet = stylesheet.replace("@@media", "@media")
    checks = 0
    with sync_playwright() as playwright:
        browser = playwright.chromium.launch(headless=True)
        page = browser.new_page()
        for name in ("skill-editor", "skill-editor-advanced"):
            fixture = (fixture_directory / f"{name}.html").read_text(encoding="utf-8")
            document = (
                '<!DOCTYPE html><html><head><meta name="viewport" content="width=device-width, initial-scale=1">'
                f'<style>{stylesheet}</style></head><body><main><section class="card skill-editor">'
                f'{fixture}</section></main></body></html>'
            )
            for width in (1480, 1024, 900, 768, 375):
                page.set_viewport_size({"width": width, "height": 1100})
                page.set_content(document)
                for expanded in (False, True):
                    page.locator(".skill-search").evaluate_all(
                        "(elements, open) => elements.forEach(element => element.open = open)", expanded
                    )
                    # Include advanced stored-value rows, which start inside a closed details element.
                    if name.endswith("advanced"):
                        page.locator("details").evaluate_all(
                            "elements => elements.filter(element => element.querySelector('.skill-input-row'))"
                            ".forEach(element => element.open = true)"
                        )
                    rows = page.locator(".skill-control-row")
                    assert rows.count() == (6 if name.endswith("advanced") else 3)
                    for row in rows.all():
                        skill = row.locator("select").bounding_box()
                        numbers = row.locator('input[type="number"]')
                        level = numbers.bounding_box() if numbers.count() else None
                        remove = row.locator(":scope > button").bounding_box()
                        if width > 900:
                            assert abs(skill["y"] - remove["y"]) < 1, (name, width, expanded, skill, remove)
                            assert abs(skill["height"] - remove["height"]) < 1
                            if level:
                                assert abs(skill["y"] - level["y"]) < 1, (width, expanded, skill, level)
                                assert level["x"] >= skill["x"] + skill["width"]
                                assert remove["x"] >= level["x"] + level["width"]
                        else:
                            if level:
                                assert abs(skill["x"] - level["x"]) < 1
                                assert level["y"] >= skill["y"] + skill["height"]
                            previous = level or skill
                            assert remove["y"] >= previous["y"] + previous["height"]
                        checks += 1
                    if name == "skill-editor":
                        assert page.locator('[data-bonus="ore-mining-yield"] input').count() == 1
                        assert page.locator(".skill-preview").evaluate("element => element.scrollWidth <= element.clientWidth")
                    assert page.evaluate("document.documentElement.scrollWidth <= window.innerWidth")
                    overflowing = page.locator(".skill-editor").evaluate(
                        """editor => [...editor.querySelectorAll('input, select, button')].filter(element => {
                            const bounds = element.getBoundingClientRect();
                            const parent = editor.getBoundingClientRect();
                            return bounds.width && (bounds.left < parent.left || bounds.right > parent.right);
                        }).map(element => element.outerHTML)"""
                    )
                    assert not overflowing, (name, width, expanded, overflowing)
                    if not expanded:
                        page.screenshot(path=str(fixture_directory / f"{name}-{width}.png"), full_page=True)
        browser.close()
    print(f"Passed {checks} row checks across both views and five widths, with search collapsed and expanded; no overflow.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("fixture_directory", type=Path)
    check_layout(parser.parse_args().fixture_directory.resolve())
