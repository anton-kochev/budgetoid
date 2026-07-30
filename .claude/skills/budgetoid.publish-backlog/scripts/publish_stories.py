#!/usr/bin/env python3
"""Publish user stories from a JSON manifest to a GitHub Projects v2 board.

Creates each story as a draft item, sets Status=Backlog and the story's
Priority. Titles that already exist in the project are skipped, so the
script is safe to re-run after a partial failure.

Manifest shape:
    {
      "stories": [
        {"title": "Story 1.1: ...", "priority": "P0", "body": "markdown..."}
      ]
    }
"""
import argparse
import json
import subprocess
import sys


def sh(args):
    result = subprocess.run(args, capture_output=True, text=True)
    if result.returncode != 0:
        sys.exit(f"command failed: {' '.join(args)}\n{result.stderr}")
    return result.stdout


def resolve_fields(project, owner):
    view = json.loads(sh(["gh", "project", "view", project, "--owner", owner, "--format", "json"]))
    fields = json.loads(sh(["gh", "project", "field-list", project, "--owner", owner, "--format", "json"]))["fields"]
    by_name = {f["name"]: f for f in fields}

    def option_id(field_name, option_name):
        field = by_name[field_name]
        for option in field["options"]:
            if option["name"] == option_name:
                return field["id"], option["id"]
        sys.exit(f"option '{option_name}' not found in field '{field_name}'")

    return view["id"], by_name, option_id


def existing_titles(project, owner):
    items = json.loads(sh([
        "gh", "project", "item-list", project, "--owner", owner,
        "--format", "json", "--limit", "200",
    ]))["items"]
    return {i["content"].get("title", "") for i in items}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, help="path to the stories JSON manifest")
    parser.add_argument("--owner", default="anton-kochev")
    parser.add_argument("--project", default="1")
    args = parser.parse_args()

    with open(args.manifest) as f:
        stories = json.load(f)["stories"]

    project_id, fields, option_id = resolve_fields(args.project, args.owner)
    status_field, backlog = option_id("Status", "Backlog")
    present = existing_titles(args.project, args.owner)

    for story in stories:
        title = story["title"]
        if title in present:
            print(f"skipped (exists): {title}")
            continue
        created = json.loads(sh([
            "gh", "project", "item-create", args.project, "--owner", args.owner,
            "--title", title, "--body", story["body"], "--format", "json",
        ]))
        item_id = created["id"]
        sh([
            "gh", "project", "item-edit", "--id", item_id, "--project-id", project_id,
            "--field-id", status_field, "--single-select-option-id", backlog,
        ])
        priority = story.get("priority")
        if priority:
            priority_field, option = option_id("Priority", priority)
            sh([
                "gh", "project", "item-edit", "--id", item_id, "--project-id", project_id,
                "--field-id", priority_field, "--single-select-option-id", option,
            ])
        print(f"created: {title} [{priority or 'no priority'}]")

    print("done")


if __name__ == "__main__":
    main()
