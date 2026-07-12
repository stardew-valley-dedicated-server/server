---
paths:
  - "docker/**/Dockerfile*"
---

# Removing image packages can silently drop the app's transitive runtime dependencies

When removing apt/apk packages from an image, boot the produced image before shipping — a green build proves nothing about runtime dependencies that were only present transitively. The app's true runtime deps must be explicit in the install list.

**Why:** The GUI-cleanup pass removed polybar/rofi/etc. from `docker/Dockerfile`, and `libicu67` vanished with them — it was only present as a transitive dependency of the removed stack. SMAPI's .NET runtime FailFasts at startup without ICU (`Process terminated. Couldn't find a valid ICU package`), so every server boot in the next run failed while the build stayed green. The dependency is now declared explicitly with a comment naming its consumer.

**How to apply:** After any package removal, run the image far enough that the app process actually starts (not just container init). When adding a runtime dependency, install it explicitly with a comment naming the consumer, so a future removal of an unrelated package can't orphan it. Sibling of `verify-edit-landed-in-artifact.md` (build green ≠ artifact correct); this is the runtime-dependency case.
