import { createRouter, createWebHashHistory } from "vue-router";

// Hash history so routing works identically in all three hosting modes: live
// (Kestrel at /), offline report (file://), and the deep R2 object key. The
// fragment is client-only, so no server rewrite is needed anywhere. Routes
// render nothing — App.vue is the single shell, and useRouteSync maps the URL
// onto the existing view/selection/inspect state (see useRouteSync.ts).
//
// The container-inspect modal is NOT a route here: it overlays whichever base
// view is active, so it's carried as a `?inspect=<instanceId>` query that
// composes with any of these paths (mirrors how InstanceInspect overlays both
// views in the UI). A nested /vnc/:id path would wrongly bind the modal to VNC
// and strand report-mode deep-links, where the modal is opened from the tests
// view.
const noRender = { render: () => null };

const router = createRouter({
    history: createWebHashHistory(),
    routes: [
        { path: "/", name: "tests", component: noRender },
        { path: "/overview", name: "overview", component: noRender },
        { path: "/tests/:displayName", name: "test", component: noRender },
        { path: "/vnc", name: "vnc", component: noRender },
        { path: "/:pathMatch(.*)*", redirect: "/" },
    ],
});

export default router;
