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
├── tsconfig.json
└── biome.json
```
