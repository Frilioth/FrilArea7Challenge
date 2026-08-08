AREA 7 MENU VIDEO CYCLER  v1.0.0
================================

Rotates the main-menu background video through several Area 7
"security camera" feeds on a timer, like a bank of monitors
switching cameras.

CONFIG - Config/videocycler.xml
  interval = seconds per feed before switching (currently 10).
  <clip>   = one feed. uri is a mod path with NO extension
             (the game appends .mp4 on Windows).

ADDING / CHANGING CAMERAS (no rebuild needed)
  1. Make the clip: MP4, H.264, 1920x1080, 30fps, muted, looping.
  2. Put it in FrilArea7Logo/Video/  e.g. area7_menu_cam8.mp4
  3. Add a line to videocycler.xml:
        <clip uri="@modfolder(FrilArea7Logo):Video/area7_menu_cam8"/>
  4. Restart.
  Names are up to you - the uri just has to match the real filename
  (minus extension). A listed-but-missing file is skipped harmlessly.
  No hard cap on feed count; keep it sensible (each 1080p clip is a
  real file the menu loads).

REQUIREMENTS
  DLL mod - needs EAC OFF, same as the other Area 7 DLL mods.
  The video files live in FrilArea7Logo/Video/ (shared with the
  static menu-background swap).

CHANGELOG
  1.0.0 - first release. Config-driven clip list + interval.
          Confirmed working with 7 feeds at 10s each.
