# Changelog

## [1.1.0](https://github.com/stardew-valley-dedicated-server/server/compare/v1.0.2...v1.1.0) (2026-01-03)


### Features

* **build:** add Directory.Build.props for centralized .NET build configuration ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **build:** add download-game.sh script for automated game file retrieval ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **build:** update Makefile with Docker build integration and secrets handling ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **ci:** add automated CI/CD pipeline and build system ([#66](https://github.com/stardew-valley-dedicated-server/server/issues/66)) ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **ci:** add pr-validation workflow for build checks on pull requests ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **ci:** add preview-build workflow for automated Docker image publishing ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **ci:** add release workflow with release-please automation ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **ci:** configure release-please for semantic versioning and changelog generation ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **cli:** [#34](https://github.com/stardew-valley-dedicated-server/server/issues/34) Support SMAPI CLI input when running inside docker ([#70](https://github.com/stardew-valley-dedicated-server/server/issues/70)) ([7f3430c](https://github.com/stardew-valley-dedicated-server/server/commit/7f3430c2409ee599428b2fce0cf7a3a6d643c88d))
* **docker:** add runtime credential handling with secrets and env var support ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **docker:** consolidate SMAPI configuration into single config file ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **docker:** improve Dockerfile with multi-stage build and secrets support ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **docker:** update docker-compose.yml for simplified deployment ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **server:** Implement DLL patcher to modify code before SDV initialization ([#71](https://github.com/stardew-valley-dedicated-server/server/issues/71)) ([460d3a8](https://github.com/stardew-valley-dedicated-server/server/commit/460d3a8960a46831436bd928a09b372fbb165bef))
* **server:** implement Galaxy auth without Steam client for farmhand ownership ([#72](https://github.com/stardew-valley-dedicated-server/server/issues/72)) ([d01c32d](https://github.com/stardew-valley-dedicated-server/server/commit/d01c32d2b4162f5e161a1be6f829c37954b5356a))
* **tools:** Implement pathoschild xnb unpacker container ([#73](https://github.com/stardew-valley-dedicated-server/server/issues/73)) ([9ae4c23](https://github.com/stardew-valley-dedicated-server/server/commit/9ae4c2358d36dbf79fae8d763f09f4c56e4d8bbb))


### Bug Fixes

* **docker:** remove mod folder from .dockerignore ([#68](https://github.com/stardew-valley-dedicated-server/server/issues/68)) ([d901740](https://github.com/stardew-valley-dedicated-server/server/commit/d901740b0ffa764bcb260fe1856ba5551bde4116))
* **docker:** remove tools folder from .dockerignore ([#69](https://github.com/stardew-valley-dedicated-server/server/issues/69)) ([198bdec](https://github.com/stardew-valley-dedicated-server/server/commit/198bdec9e72e21d30a9c8314c6f20cf666427234))
* **gameplay:** Fix communitycenter runs to not buy joja membership ([#75](https://github.com/stardew-valley-dedicated-server/server/issues/75)) ([101a752](https://github.com/stardew-valley-dedicated-server/server/commit/101a7523e377b9a75afcaa603de74de8f819da15))
