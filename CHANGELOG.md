# Changelog

## [1.3.0](https://github.com/stardew-valley-dedicated-server/server/compare/sdvd-server-v1.2.0...sdvd-server-v1.3.0) (2026-01-17)


### Features

* **auth:** add invite code system and improved steam authentication ([b38c85f](https://github.com/stardew-valley-dedicated-server/server/commit/b38c85fcbd5ce958a3e79eae0987f5e268b9c143))
* **cabin:** improve cabin management and add invite code command ([fdbefee](https://github.com/stardew-valley-dedicated-server/server/commit/fdbefee6e4b005953f1ef6048187c48fd5d56864))
* **ci:** add build and push workflows for steam-service image ([#91](https://github.com/stardew-valley-dedicated-server/server/issues/91)) ([b864de1](https://github.com/stardew-valley-dedicated-server/server/commit/b864de1fb35b0470d6bcb3103c0ff71bd4090428))
* **ci:** add write permissions for preview tag updates ([#90](https://github.com/stardew-valley-dedicated-server/server/issues/90)) ([ee0b407](https://github.com/stardew-valley-dedicated-server/server/commit/ee0b407f52415d9f90ac3c02882295c5a1b9105f))
* **ci:** reset preview version counter per release ([#89](https://github.com/stardew-valley-dedicated-server/server/issues/89)) ([3f077d8](https://github.com/stardew-valley-dedicated-server/server/commit/3f077d8f035110241c6e87075c0c7a25f9063a18))
* **ci:** sync readme to docker hub on release ([#88](https://github.com/stardew-valley-dedicated-server/server/issues/88)) ([2c5e093](https://github.com/stardew-valley-dedicated-server/server/commit/2c5e0936febe2876f87c18c6f7c7c197aad7d8f8))
* **cli:** enhance terminal interface with memory monitoring ([d422b0b](https://github.com/stardew-valley-dedicated-server/server/commit/d422b0b7bcfcac0b1ec8f2232823c72fe285ddf0))
* **docker:** optimize build process and simplify configuration ([6bd1ecd](https://github.com/stardew-valley-dedicated-server/server/commit/6bd1ecde2736cfc337ec6706bfa42bd73b01512e))
* **mod:** add server banner and improve core services ([8cd56d2](https://github.com/stardew-valley-dedicated-server/server/commit/8cd56d20ad3bde5f618a58cd0ab5f005190a01e5))


### Bug Fixes

* **docs:** sync documentation with actual implementation ([#92](https://github.com/stardew-valley-dedicated-server/server/issues/92)) ([00b1749](https://github.com/stardew-valley-dedicated-server/server/commit/00b174924b872db5c7779e38c190b9d80be137b2))
* **steam-service:** fallback to download without cdn auth token ([7c75633](https://github.com/stardew-valley-dedicated-server/server/commit/7c75633899fa089bf1bc80cec0de47ace0f10a2b))
* **steam-service:** improve cdn auth with retry logic and license check ([56010ba](https://github.com/stardew-valley-dedicated-server/server/commit/56010bafb9f3092c267a43b652fc7e475a72720b))
* **steam-service:** remove unnecessary cdn auth token logic ([3ae7a6d](https://github.com/stardew-valley-dedicated-server/server/commit/3ae7a6d963ccda39984b8b4857d1a5571c09bb0d))


### Documentation

* migrate documentation to vitepress ([8f0872f](https://github.com/stardew-valley-dedicated-server/server/commit/8f0872fe44912c02f5991ccc7d2253de1270778e))

## [1.2.0](https://github.com/stardew-valley-dedicated-server/server/compare/sdvd-server-v1.1.0...sdvd-server-v1.2.0) (2026-01-07)


### Features

* add decompile and SDV/SMAPI version checking tools ([3042887](https://github.com/stardew-valley-dedicated-server/server/commit/30428876bb48544aacbedd8c837082bceea49eda))
* add script for interactive start ([400a3f0](https://github.com/stardew-valley-dedicated-server/server/commit/400a3f01133386b32f7da98b6dbad968c69d5be1))
* **build:** add Directory.Build.props for centralized .NET build configuration ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **build:** add download-game.sh script for automated game file retrieval ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **build:** update Makefile with Docker build integration and secrets handling ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **ci:** add automated CI/CD pipeline and build system ([#66](https://github.com/stardew-valley-dedicated-server/server/issues/66)) ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **ci:** add pr-validation workflow for build checks on pull requests ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **ci:** add preview-build workflow for automated Docker image publishing ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **ci:** add release workflow with release-please automation ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **ci:** configure release-please for semantic versioning and changelog generation ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **cli:** [#34](https://github.com/stardew-valley-dedicated-server/server/issues/34) Support SMAPI CLI input when running inside docker ([#70](https://github.com/stardew-valley-dedicated-server/server/issues/70)) ([7f3430c](https://github.com/stardew-valley-dedicated-server/server/commit/7f3430c2409ee599428b2fce0cf7a3a6d643c88d))
* **cli:** upgrade tmux to version 3.6a for better terminal support ([#84](https://github.com/stardew-valley-dedicated-server/server/issues/84)) ([ea27a20](https://github.com/stardew-valley-dedicated-server/server/commit/ea27a2083c5872b44c1475b42dd64db1851948a1))
* **docker:** add runtime credential handling with secrets and env var support ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **docker:** consolidate SMAPI configuration into single config file ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **docker:** improve Dockerfile with multi-stage build and secrets support ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **docker:** update docker-compose.yml for simplified deployment ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **server:** Implement DLL patcher to modify code before SDV initialization ([#71](https://github.com/stardew-valley-dedicated-server/server/issues/71)) ([460d3a8](https://github.com/stardew-valley-dedicated-server/server/commit/460d3a8960a46831436bd928a09b372fbb165bef))
* **server:** implement Galaxy auth without Steam client for farmhand ownership ([#72](https://github.com/stardew-valley-dedicated-server/server/issues/72)) ([d01c32d](https://github.com/stardew-valley-dedicated-server/server/commit/d01c32d2b4162f5e161a1be6f829c37954b5356a))
* **tools:** Implement pathoschild xnb unpacker container ([#73](https://github.com/stardew-valley-dedicated-server/server/issues/73)) ([9ae4c23](https://github.com/stardew-valley-dedicated-server/server/commit/9ae4c2358d36dbf79fae8d763f09f4c56e4d8bbb))


### Bug Fixes

* add required scripts which where accidentally ignored ([43bd2b0](https://github.com/stardew-valley-dedicated-server/server/commit/43bd2b098977495bf6928576985b7e20bc1ed633))
* add required scripts which where accidentally ignored ([669e117](https://github.com/stardew-valley-dedicated-server/server/commit/669e117a0a085d5364a2f075b222b587770e4985))
* **docker:** remove mod folder from .dockerignore ([#68](https://github.com/stardew-valley-dedicated-server/server/issues/68)) ([d901740](https://github.com/stardew-valley-dedicated-server/server/commit/d901740b0ffa764bcb260fe1856ba5551bde4116))
* **docker:** remove tools folder from .dockerignore ([#69](https://github.com/stardew-valley-dedicated-server/server/issues/69)) ([198bdec](https://github.com/stardew-valley-dedicated-server/server/commit/198bdec9e72e21d30a9c8314c6f20cf666427234))
* enforce LF line endings for container compatibility ([b49ca78](https://github.com/stardew-valley-dedicated-server/server/commit/b49ca78e207a0a653a8beffa1011bc9870c6cf1f))
* enforce LF line endings for container compatibility ([98dff61](https://github.com/stardew-valley-dedicated-server/server/commit/98dff61bfd39326016bdf0a6e5d0a8ed58dd9356))
* enforce LF line endings for container compatibility, [#2](https://github.com/stardew-valley-dedicated-server/server/issues/2) ([59ce900](https://github.com/stardew-valley-dedicated-server/server/commit/59ce9002d47259bfca1f8fe2f7153234d3d3857a))
* fix LF line endings and add editorconfig ([7f7f6a5](https://github.com/stardew-valley-dedicated-server/server/commit/7f7f6a5378513dee728f9a1d048d84f7b9929730))
* **gameplay:** Fix communitycenter runs to not buy joja membership ([#75](https://github.com/stardew-valley-dedicated-server/server/issues/75)) ([101a752](https://github.com/stardew-valley-dedicated-server/server/commit/101a7523e377b9a75afcaa603de74de8f819da15))
* gitattributes typo and formatting ([f28ef45](https://github.com/stardew-valley-dedicated-server/server/commit/f28ef45bee8457564dcc48fa91573fdf7699b793))
* gitattributes typo and formatting ([513860d](https://github.com/stardew-valley-dedicated-server/server/commit/513860d7702df2dcf5274361d043f0d54f1f0de5))
* **mods:** ensure extra/custom mods load properly again ([#22](https://github.com/stardew-valley-dedicated-server/server/issues/22)) ([165ae18](https://github.com/stardew-valley-dedicated-server/server/commit/165ae184d074f4ebd30dd348507836e8f89d9757))
* prevent changing EOL for images ([1655124](https://github.com/stardew-valley-dedicated-server/server/commit/1655124a0e2206909cbb6c7cd6eb4b4d6a79019f))
* prevent changing EOL for images ([927029e](https://github.com/stardew-valley-dedicated-server/server/commit/927029ec7e840aa5035071068787b4a3051cdfe6))
* readme typo in mod version badge ([e531d27](https://github.com/stardew-valley-dedicated-server/server/commit/e531d27305a71bb411a542999929ffe43197299e))
* readme typo in mod version badge ([cf83810](https://github.com/stardew-valley-dedicated-server/server/commit/cf838107e326ce8b3aa22ea979751293f6d6635a))
* **server:** cabin management and other misc stuff ([#24](https://github.com/stardew-valley-dedicated-server/server/issues/24)) ([5619c44](https://github.com/stardew-valley-dedicated-server/server/commit/5619c441cb4b95611a77b3550e3785250065afda))
* **server:** FarmhouseStack CabinStrategy works again ([#28](https://github.com/stardew-valley-dedicated-server/server/issues/28)) ([42d0c22](https://github.com/stardew-valley-dedicated-server/server/commit/42d0c2233d55c2160afc9de90c87ce31bb038093))


### Documentation

* add more build instructions and working discord invite link ([79dea9d](https://github.com/stardew-valley-dedicated-server/server/commit/79dea9d2458c58920ec0c89b574ac2b9f2f56ba8))
* **ci:** add comprehensive CI/CD and release documentation ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **ci:** update CONTRIBUTING.md with quick start and conventional commits ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **ci:** update contribution guide with GitHub Flow workflow ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* **config:** update configuration guide for new setup ([0d87213](https://github.com/stardew-valley-dedicated-server/server/commit/0d87213246ac28311ca076a2658bef290dc437ed))
* convert planned features section into github issues ([#48](https://github.com/stardew-valley-dedicated-server/server/issues/48)) ([395624a](https://github.com/stardew-valley-dedicated-server/server/commit/395624afd5c7999a202b094b31c6320f09ce4af1))
* update develop/setup instructions ([36aff58](https://github.com/stardew-valley-dedicated-server/server/commit/36aff58fe5f169b636e36e85e9a851216f165f8b))
* update wording and badges in preparation for first release ([4d4bc45](https://github.com/stardew-valley-dedicated-server/server/commit/4d4bc45d29a08a169b95761d9287f68daf575cde))

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
