# JunimoServer Documentation

VitePress-based documentation site for JunimoServer.

## Development

```bash
# Install dependencies
npm install

# Start dev server (http://localhost:5173)
npm run dev

# Build for production
npm run build

# Preview production build
npm run preview
```

## Server Status Widget

`npm run dev` serves a built-in fake `/status`, so the card on the Public Test Server page works
offline. To point dev or a local build at a real server instead, create `docs/.env.local`:

```sh
DOCS_TEST_SERVER_API_URL=https://203.0.113.10/preview
```

CI sets the same variable from the `DOCS_TEST_SERVER_API_URL` repository variable. A build
without it ships the page with no card.

## Quality Checks

```bash
# Run all checks (lint + typecheck)
npm run check

# Lint only
npm run lint

# Auto-fix linting issues
npm run lint:fix

# Type check only
npm run typecheck
```

## Project Structure

```
website/
├── docs/                    # Documentation content
│   ├── .vitepress/
│   │   └── config.ts       # VitePress configuration
│   ├── index.md            # Homepage
│   └── guide.md            # Getting started guide
├── package.json
└── tsconfig.json
```
