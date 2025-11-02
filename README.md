# SceneLoader

A simple **BepInEx plugin** for _Football Manager 2026_ that lets you load a custom Unity scene in-game.

Feel free to use, modify or whatever its a proof of concept

---

## Current known problems

- Shaders are not loading correctly so materials are pink
- You can't add your own lights because of how they setup lighting

---

## Installation

1. Install **BepInEx**
2. Put `SceneLoader.dll` in:

   ```
   <FM26 folder>\BepInEx\plugins\
   ```

3. Create a folder named `bundles`:

   ```
   <FM26 folder>\BepInEx\plugins\bundles\
   ```

4. With Unity create a .bundle with the scene named stadium.bundle and drop it inside:

   ```
   <FM26 folder>\BepInEx\plugins\bundles\
   ```

---

## How to Use

- Start _Football Manager 2026_ with BepInEx
- During a **match**, press **F8** to load the scene
- The plugin will load the stadium inside the .bundle

  ```
  BepInEx\plugins\bundles\stadium.bundle
  ```

---
