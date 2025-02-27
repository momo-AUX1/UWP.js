#!/usr/bin/env python3

import os
import zipfile
import json
import colorama
import re
import sys
import wget
import shutil
from PIL import Image
import secrets
from colorama import Fore, Style

colorama.init(autoreset=True)

isSPA = False
isCapacitor = False
spaDATA = None
capDATA = None
current_web_dir = None
resources_dir = None
__version__ = "0.0.1"
__author__ = "momo-AUX1"

def parsecapacitor(path):
    with open(path, 'r') as file:
        file_content = file.read()
    config_match = re.search(r'const config:.*?= ({.*});', file_content, re.DOTALL)
    if config_match:
        config_str = config_match.group(1)
        config_str = re.sub(r'([a-zA-Z0-9_]+):', r'"\1":', config_str)
        config_str = config_str.replace("'", '"')

        try:
            config_json = json.loads(config_str)
            return config_json
        except json.JSONDecodeError as e:
            print(Fore.RED + f"❌ Error parsing JSON: {e}")
            print("Transformed string:", config_str)

def replace_in_files(directory, old_str, new_str):
    """Recursively replace occurrences of old_str with new_str in all files under directory."""
    for root, dirs, files in os.walk(directory):
        for file_name in files:
            file_path = os.path.join(root, file_name)
            try:
                with open(file_path, 'r', encoding='utf-8', errors='ignore') as f:
                    content = f.read()
                new_content = content.replace(old_str, new_str)
                if new_content != content:
                    with open(file_path, 'w', encoding='utf-8', errors='ignore') as f:
                        f.write(new_content)
            except Exception as e:
                print(Fore.YELLOW + f"⚠️  Skipping file '{file_path}': {e}")

def is_valid_aspect_ratio(width, height, target_ratio=2.067, tolerance=1.5):
    if height == 0:
        return False
    aspect_ratio = width / height
    lower_bound = target_ratio - tolerance
    upper_bound = target_ratio + tolerance
    return lower_bound <= aspect_ratio <= upper_bound

def initialize_uwpjs():
    files = os.listdir('.')

    global isSPA, spaDATA, isCapacitor, capDATA, current_web_dir, resources_dir

    if 'package.json' in files:
        isSPA = True
        with open('package.json', 'r') as f:
            spaDATA = json.load(f)
    else:
        print(Fore.RED + "❌ No valid project found. exiting...")
        print(Fore.YELLOW + "❔ If you don't use a JS framework, try `npm init` to create a package.json.")
        sys.exit(1)

    if 'capacitor.config.ts' in files:
        isCapacitor = True
        capDATA = parsecapacitor('capacitor.config.ts')

    if capDATA and capDATA.get('webDir'):
        current_web_dir = capDATA['webDir']
    elif not capDATA and "dist" in files:
        current_web_dir = "dist"
    elif not capDATA and "build" in files:
        current_web_dir = "build"
    else:
        current_web_dir = None

    if "resources" in files:
        resources_dir = "resources"
    elif "assets" in files:
        resources_dir = "assets"
    else:
        resources_dir = None

    suggested_name = capDATA['appName'] if capDATA and 'appName' in capDATA else (spaDATA['name'] if spaDATA and 'name' in spaDATA else "MyApp")
    suggested_build_dir = current_web_dir if current_web_dir else "dist"
    suggested_resources_dir = resources_dir if resources_dir else "assets"

    project_name = input(Fore.CYAN + f"ℹ️  What should we call your app? ({suggested_name}): ") or suggested_name
    build_dir_name = input(Fore.CYAN + f"ℹ️  What is the name of your build directory? ({suggested_build_dir}): ") or suggested_build_dir
    resources_dir_name = input(Fore.CYAN + f"ℹ️  What is the name of your resources directory? ({suggested_resources_dir}): ") or suggested_resources_dir

    user_name = os.getlogin()

    with open('uwp_js.config.json', 'w') as f:
        f.write(json.dumps({
            "name": project_name,
            "buildDir": build_dir_name,
            "resourcesDir": resources_dir_name,
            "user": user_name,
            "version": __version__,
            "platforms": {
                "uwp": {
                    "config": "uwp_js.config.json"
                }
            }
        }, indent=2))

    print(Fore.GREEN + "🚀 Downloading UWP.js template...")
    if not os.path.exists("uwp"):
        os.mkdir("uwp")
    os.chdir("uwp")
    wget.download("https://git.nanodata.cloud/moonpower/uwpjs/raw/branch/main/UWP.js.zip", out="UWP.js.zip")
    print("\n" + Fore.GREEN + "🎉 Download complete!")

    print(Fore.GREEN + "🔧 Extracting UWP.js template...")
    with zipfile.ZipFile("UWP.js.zip", "r") as zip_ref:
        zip_ref.extractall(".")
    os.remove("UWP.js.zip")

    if os.path.exists("UWP.js") and os.path.isdir("UWP.js"):
        os.rename("UWP.js", project_name)

    root_sln = "UWP.js.sln"
    new_root_sln = f"{project_name}.sln"
    if os.path.exists(root_sln):
        os.rename(root_sln, new_root_sln)

    csproj_path = os.path.join(project_name, "UWP.js.csproj")
    if os.path.exists(csproj_path):
        os.rename(csproj_path, os.path.join(project_name, f"{project_name}.csproj"))

    if os.path.exists(new_root_sln):
        with open(new_root_sln, 'r', encoding='utf-8', errors='ignore') as f:
            sln_content = f.read()
        sln_content = sln_content.replace("UWP.js\\UWP.js.csproj", f"{project_name}\\{project_name}.csproj")
        sln_content = sln_content.replace("UWP.js.sln", f"{project_name}.sln")
        sln_content = sln_content.replace("UWP.js", project_name)
        with open(new_root_sln, 'w', encoding='utf-8', errors='ignore') as f:
            f.write(sln_content)

    print(Fore.BLUE + "🔄 Replacing placeholders...")
    replace_in_files(project_name, "UWP.js", project_name)
    replace_in_files(project_name, "Naalf", user_name)
    replace_in_files(project_name, "4e34859b-2064-4d01-a9c6-f43ce8241ecd", f"{secrets.token_hex(4)}-{secrets.token_hex(2)}-{secrets.token_hex(2)}-{secrets.token_hex(2)}-{secrets.token_hex(6)}")

    print(Fore.GREEN + "✅ Initialization complete! Run sync next to prepare your UWP.js project.")

def sync_project():
    try:
        with open('uwp_js.config.json', 'r') as f:
            data = json.load(f)
    except FileNotFoundError:
        print(Fore.RED + "❌ Configuration file 'uwp_js.config.json' not found. Please initialize the project first.")
        sys.exit(1)
    except json.JSONDecodeError as e:
        print(Fore.RED + f"❌ Error parsing 'uwp_js.config.json': {e}")
        sys.exit(1)

    project_name = data.get('name')
    build_dir = data.get('buildDir')
    resources_dir = data.get('resourcesDir')
    user_name = data.get('user')

    if not project_name or not build_dir or not resources_dir or not user_name:
        print(Fore.RED + "❌ Invalid configuration data. Please re-initialize the project.")
        sys.exit(1)

    if not os.path.exists(build_dir):
        print(Fore.RED + f"❌ Build directory '{build_dir}' does not exist. Please build your project first.")
        sys.exit(1)

    uwp_assets_wp_dir = os.path.join("uwp", project_name, "Assets", "WP")
    if not os.path.exists(uwp_assets_wp_dir):
        os.makedirs(uwp_assets_wp_dir)

    print(Fore.GREEN + f"🚀 Syncing build directory '{build_dir}' to 'uwp/{project_name}/Assets/WP'...")

    for root, dirs, files in os.walk(uwp_assets_wp_dir):
        for file in files:
            try:
                os.remove(os.path.join(root, file))
            except Exception as e:
                print(Fore.YELLOW + f"⚠️  Could not remove file '{file}': {e}")
        for d in dirs:
            try:
                shutil.rmtree(os.path.join(root, d))
            except Exception as e:
                print(Fore.YELLOW + f"⚠️  Could not remove directory '{d}': {e}")

    if os.path.isdir(build_dir):
        for item in os.listdir(build_dir):
            s = os.path.join(build_dir, item)
            d = os.path.join(uwp_assets_wp_dir, item)
            try:
                if os.path.isdir(s):
                    shutil.copytree(s, d, dirs_exist_ok=True)
                else:
                    shutil.copy2(s, d)
            except Exception as e:
                print(Fore.YELLOW + f"⚠️  Could not copy '{s}' to '{d}': {e}")
    else:
        print(Fore.RED + f"❌ '{build_dir}' is not a directory.")
        sys.exit(1)

    images = {
        "LockScreenLogo.scale-200.png": (48, 48),
        "Square44x44Logo.scale-200.png": (88, 88),
        "Square44x44Logo.targetsize-24_altform-unplated.png": (24, 24),
        "Square150x150Logo.scale-200.png": (300, 300),
        "StoreLogo.png": (50, 50)
    }

    banners = {
        "SplashScreen.scale-200.png": (1240, 600),
        "Wide310x150Logo.scale-200.png": (620, 300)
    }

    if resources_dir and os.path.exists(resources_dir):
        icon_candidates = [
            os.path.join(resources_dir, "icon.png"),
            os.path.join(resources_dir, "icon.jpg"),
            os.path.join(resources_dir, "icon.jpeg"),
            os.path.join(resources_dir, "logo.png"),
            os.path.join(resources_dir, "logo.jpg"),
             os.path.join(resources_dir, "logo.jpeg"),
            os.path.join(resources_dir, "icon-only.png"),
            os.path.join(resources_dir, "icon-only.jpg"),
        ]

        banner_candidates = [
            os.path.join(resources_dir, "banner.png"),
            os.path.join(resources_dir, "banner.jpg"),
            os.path.join(resources_dir, "banner.jpeg"),
        ]

        print(Fore.BLUE + f"📁 Resources directory: {resources_dir}")

        chosen_logo = None
        for candidate in icon_candidates:
            print(Fore.BLUE + f"🔍 Checking for icon at: {candidate}")
            if os.path.exists(candidate):
                chosen_logo = candidate
                print(Fore.GREEN + f"✅ Found icon: {candidate}")
                break

        chosen_banner = None
        for candidate in banner_candidates:
            print(Fore.BLUE + f"🔍 Checking for banner at: {candidate}")
            if os.path.exists(candidate):
                chosen_banner = candidate
                print(Fore.GREEN + f"✅ Found banner: {candidate}")
                break

        if chosen_logo:
            try:
                with Image.open(chosen_logo) as img:
                    w, h = img.size
                if w == h:
                    print(Fore.GREEN + "🖼  Found a square icon. Resizing for UWP...")
                    for img_name, (tw, th) in images.items():
                        with Image.open(chosen_logo) as img:
                            resized_img = img.resize((tw, th), Image.Resampling.LANCZOS)
                            dest_path = os.path.join("uwp", project_name, "Assets", img_name)
                            resized_img.save(dest_path)
                            print(Fore.GREEN + f"✅ Saved resized icon: {dest_path}")
                else:
                    print(Fore.YELLOW + "⚠️  The found icon is not square. Skipping UWP icon generation.")
            except Exception as e:
                print(Fore.RED + f"❌ Error processing icon '{chosen_logo}': {e}")
        else:
            print(Fore.YELLOW + "⚠️  No valid icon named icon.png, icon.jpg, logo.png, logo.jpg, icon-only.png, or icon-only.jpg found.")

        if chosen_banner:
            try:
                with Image.open(chosen_banner) as img:
                    w, h = img.size
                if is_valid_aspect_ratio(w, h):
                    aspect_ratio = w / h
                    print(Fore.GREEN + f"🖼  Valid banner found with aspect ratio {aspect_ratio:.2f}. Resizing for UWP...")
                    for banner_name, (tw, th) in banners.items():
                        with Image.open(chosen_banner) as img:
                            resized_img = img.resize((tw, th), Image.Resampling.LANCZOS)
                            dest_path = os.path.join("uwp", project_name, "Assets", banner_name)
                            resized_img.save(dest_path)
                            print(Fore.GREEN + f"✅ Saved resized banner: {dest_path}")
                else:
                    aspect_ratio = w / h
                    print(Fore.YELLOW + f"⚠️  Banner aspect ratio {aspect_ratio:.2f} is out of the valid range. Skipping UWP banner generation.")
            except Exception as e:
                print(Fore.RED + f"❌ Error processing banner '{chosen_banner}': {e}")
        else:
            print(Fore.YELLOW + "⚠️  No valid banner named banner.png or banner.jpg found.")

    else:
        print(Fore.YELLOW + "⚠️  Resources directory not found or not specified. No images synced.")

    print(Fore.GREEN + f"✅ Sync complete! Your UWP.js project '{project_name}' is now up-to-date.")

def main():
    args = sys.argv
    if len(args) < 2:
        print(Fore.RED + "❌ No command provided. Use help or -h for help.")
        sys.exit(1)

    if len(args) > 3:
        print(Fore.RED + "❌ Too many arguments provided. Use help or -h for help.")
        sys.exit(1)

    cmd = args[1]

    if cmd in ["help", "-h"]:
        print(Fore.CYAN + "ℹ️  Available commands:")
        print(Fore.CYAN + "   init, -i   : Initialize UWP.js for your project")
        print(Fore.CYAN + "   sync, -s   : Sync your project build and images to UWP.js environment")
        sys.exit(0)

    if cmd in ["init", "-i"]:
        if os.path.exists("uwp_js.config.json"):
            print(Fore.RED + "❌ UWPJS project already initialized. exiting...")
            sys.exit(1)

        if os.path.exists("uwp"):
            print(Fore.RED + "❌ UWPJS project already exists. Please remove it first. exiting...")
            sys.exit(1)
        print(Fore.BLUE + "🔧 Initializing UWPJS project...")
        initialize_uwpjs()

    elif cmd in ["sync", "-s"]:
        if not os.path.exists("uwp_js.config.json"):
            print(Fore.RED + "❌ UWPJS project not initialized. exiting...")
            sys.exit(1)
        print(Fore.BLUE + "🔄 Syncing UWPjs project...")
        sync_project()
    else:
        print(Fore.RED + f"❌ Unknown command: {cmd}. Use help or -h for help.")

if __name__ == "__main__":
    main()
