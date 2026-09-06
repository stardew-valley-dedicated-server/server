---
description: Join the public JunimoServer test server — live status, player count, and invite codes for Steam and GOG.
---

<script setup>
const apiUrl = __DOCS_TEST_SERVER_API_URL__;
</script>

# Public Test Server

The project runs a public test server so you can try JunimoServer before hosting your own. It runs the latest preview build, so expect occasional restarts and the odd rough edge. Anything that looks broken is worth a [bug report](/community/reporting-bugs).

<ServerStatusWidget v-if="apiUrl" :api-url="apiUrl" title="Public Test Server" />

## How to join

1. Copy the invite code for your platform above. Steam codes start with `S`, GOG codes with `G`.
2. In Stardew Valley, open **Co-op → Join → Enter Invite Code** and paste it.

The status refreshes every 30 seconds. **Starting** means the server is booting or loading the save, **Busy** means it is saving or changing the day, and **Offline** means it is down. All three usually clear within a few minutes.
