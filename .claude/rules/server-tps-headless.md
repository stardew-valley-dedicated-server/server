---
paths:
  - "mod/JunimoServer/Env.cs"
  - ".env.example"
  - ".env.test.example"
  - "tests/**/*.cs"
  - ".github/workflows/e2e-tests.yml"
---

# SERVER_TPS=5 is the proven-stable headless value

`SERVER_TPS=5` is proven stable for the headless server: the E2E CI workflow runs the whole suite at `SERVER_TPS: "5"` (`.github/workflows/e2e-tests.yml`, paired with `SERVER_FPS: "5"` + `CLIENT_TPS: "5"`), and the local `.env.test` uses the same. `Env.cs` clamps via `Math.Max(1, ParseInt("SERVER_TPS", 60))`, so 5 is valid (the floor is 1).

The committed `.env.example` / `.env.test.example` show a commented default `SERVER_TPS=60` and prose recommending "20-30" — conservative docs, not the proven floor; reading them as a lower bound over-provisions. Deploy uses `SERVER_FPS=0` (strictly less work than the tested `SERVER_FPS=5`), so 5 TPS is safe there too.

**How to apply:** Treat 5 as the validated headless TPS when sizing test/deploy config. Don't raise it on the `.env.example` prose alone — that's conservative, not measured.
