# Changelog

## [1.5.0](https://github.com/stardew-valley-dedicated-server/server/compare/sdvd-server-v1.4.1...sdvd-server-v1.5.0) (2026-02-11)


### Features

* add E2E integration test infrastructure ([#138](https://github.com/stardew-valley-dedicated-server/server/issues/138)) ([84d0469](https://github.com/stardew-valley-dedicated-server/server/commit/84d04693f17d33be6c98d50bf4ae8a99ddf78bb2))
* add netdebug network diagnostic tool ([#134](https://github.com/stardew-valley-dedicated-server/server/issues/134)) ([c2a5a8c](https://github.com/stardew-valley-dedicated-server/server/commit/c2a5a8c74734bf4b754aabd756c64df5adb44b3e))
* add password protection with lobby system and festival fixes ([#189](https://github.com/stardew-valley-dedicated-server/server/issues/189)) ([80a9f84](https://github.com/stardew-valley-dedicated-server/server/commit/80a9f843137acbeddfd6699cfff5d3d52f278aa3))
* add REST API service with OpenAPI support ([#139](https://github.com/stardew-valley-dedicated-server/server/issues/139)) ([9aa2d5f](https://github.com/stardew-valley-dedicated-server/server/commit/9aa2d5fcabc728589aae48a17a04d7b6dbcc1e12))
* **api:** separate Steam and GOG invite codes ([94794fa](https://github.com/stardew-valley-dedicated-server/server/commit/94794faa8046013f71de5063c389251693ebcc10))
* **auth:** refactor auth for GameServer mode and lobby management ([0541168](https://github.com/stardew-valley-dedicated-server/server/commit/0541168389b03410e5e29ad5310d0657ac31eb34))
* cabin strategy improvements ([#149](https://github.com/stardew-valley-dedicated-server/server/issues/149)) ([0446b11](https://github.com/stardew-valley-dedicated-server/server/commit/0446b11da7a710034a8eae8d7e1bbba2d6dfde0e))
* **ci:** add bootstrap option to build both docs from preview image ([#199](https://github.com/stardew-valley-dedicated-server/server/issues/199)) ([d1e742a](https://github.com/stardew-valley-dedicated-server/server/commit/d1e742a3d3bf31634cadc35cb849acb1747ae314))
* **ci:** add CODEOWNERS for automatic reviewer assignment ([cbc89de](https://github.com/stardew-valley-dedicated-server/server/commit/cbc89de3cdb2c896a6c6561250b44f5e1eea2015))
* **ci:** add Dependabot configuration for automated dependency updates ([1f9a8e4](https://github.com/stardew-valley-dedicated-server/server/commit/1f9a8e432b8bd28c1c31c0afeea70af006a3e8b5))
* **ci:** add manual preview builds and PR preview workflow ([#155](https://github.com/stardew-valley-dedicated-server/server/issues/155)) ([11ab9de](https://github.com/stardew-valley-dedicated-server/server/commit/11ab9de3730cce82c08849e28db435bd048cd837))
* **ci:** add PR auto-labeler based on changed files ([8a6dadd](https://github.com/stardew-valley-dedicated-server/server/commit/8a6dadd8a1d0c14797fdae02098ae2b6b72d1caa))
* **commands:** add new !info command to show basic server info ([#145](https://github.com/stardew-valley-dedicated-server/server/issues/145)) ([dea0c3e](https://github.com/stardew-valley-dedicated-server/server/commit/dea0c3ea227916d3b4468bfa09aaa48199ba73c5))
* **discord-bot:** add two-way chat relay and dynamic nickname ([#177](https://github.com/stardew-valley-dedicated-server/server/issues/177)) ([a3822b6](https://github.com/stardew-valley-dedicated-server/server/commit/a3822b6e4913fccc07b2ff6c3e85c91584e6bf3c))
* **discord:** improve bot reliability and startup checks ([#194](https://github.com/stardew-valley-dedicated-server/server/issues/194)) ([749a7e5](https://github.com/stardew-valley-dedicated-server/server/commit/749a7e573b2455eebbbbca5db4709231dcd76ee4))
* host automation ([#142](https://github.com/stardew-valley-dedicated-server/server/issues/142)) ([79c8a8f](https://github.com/stardew-valley-dedicated-server/server/commit/79c8a8fea697f62857832494f0ba77e8710513bb))
* **networking:** add Steam GameServer SDR networking ([380c4e3](https://github.com/stardew-valley-dedicated-server/server/commit/380c4e321c21163170a0fffd92d1700c6bd728d2))
* semi-internal discord status bot ([#127](https://github.com/stardew-valley-dedicated-server/server/issues/127)) ([484a18c](https://github.com/stardew-valley-dedicated-server/server/commit/484a18cc0f95d56f94b8469916a08d71ae5917e3))
* server and saves console commands ([#150](https://github.com/stardew-valley-dedicated-server/server/issues/150)) ([b5050fa](https://github.com/stardew-valley-dedicated-server/server/commit/b5050fa051b2ef03c8b7e371c31332338df5a1f2))
* server settings system ([#148](https://github.com/stardew-valley-dedicated-server/server/issues/148)) ([f403549](https://github.com/stardew-valley-dedicated-server/server/commit/f4035499e0530e0d016e26cb81b1df8b49714675))
* **server:** add startup validation for VNC_PASSWORD and API_KEY ([#195](https://github.com/stardew-valley-dedicated-server/server/issues/195)) ([3f3eb21](https://github.com/stardew-valley-dedicated-server/server/commit/3f3eb213e43eff1ae8f9d6d441fd9680ea61a349))
* **steam-service:** add lobby management and SDK download ([68eacbd](https://github.com/stardew-valley-dedicated-server/server/commit/68eacbdeb269ee348d309e009c874e618feab304))
* **test-client:** add Steam lobby diagnostics ([c07f4f2](https://github.com/stardew-valley-dedicated-server/server/commit/c07f4f2a77d20395f6d1379e74a163a167b60b7c))
* **test:** add e2e test orchestration MVP ([#143](https://github.com/stardew-valley-dedicated-server/server/issues/143)) ([f0989d9](https://github.com/stardew-valley-dedicated-server/server/commit/f0989d99a6ac1a790e0a225fc5447bc8f26b1f3c))
* **tests:** add containerized game client support for E2E tests ([#178](https://github.com/stardew-valley-dedicated-server/server/issues/178)) ([1dcfb4a](https://github.com/stardew-valley-dedicated-server/server/commit/1dcfb4ab2982b4d5b3fba06ef128f68540e56ee4))
* **tests:** add E2E tests for all 7 farm map types ([#180](https://github.com/stardew-valley-dedicated-server/server/issues/180)) ([3ecfa3b](https://github.com/stardew-valley-dedicated-server/server/commit/3ecfa3be439ee4adef4c634864c2fcd92dfc4f89))


### Bug Fixes

* **auth:** resolve n/a invite code by late-adding Galaxy server on au… ([#126](https://github.com/stardew-valley-dedicated-server/server/issues/126)) ([e174208](https://github.com/stardew-valley-dedicated-server/server/commit/e1742085492f5abd2a5ebecca00d87291ea3caaa))
* **ci:** add permissions to reusable workflow calls ([#193](https://github.com/stardew-valley-dedicated-server/server/issues/193)) ([ce69a23](https://github.com/stardew-valley-dedicated-server/server/commit/ce69a23d8f4db4f47c55b297e94a5fd343fb64f8))
* **ci:** fetch tags explicitly in build-preview workflow ([#201](https://github.com/stardew-valley-dedicated-server/server/issues/201)) ([c692a84](https://github.com/stardew-valley-dedicated-server/server/commit/c692a8427801a05294ff01e11064bae7c4e8faa6))
* **ci:** gracefully skip docs deployment when latest artifact missing ([#198](https://github.com/stardew-valley-dedicated-server/server/issues/198)) ([4f464ce](https://github.com/stardew-valley-dedicated-server/server/commit/4f464ce05db25018efb08718d4c1ef044ec0f6b4))
* **ci:** security hardening ([#111](https://github.com/stardew-valley-dedicated-server/server/issues/111)) ([7710524](https://github.com/stardew-valley-dedicated-server/server/commit/7710524c3a6959ca521d30930ab07a7154a80e39))
* **ci:** simplify discord notifications ([300d668](https://github.com/stardew-valley-dedicated-server/server/commit/300d66808be38c2d216d331f1de6cd523b75d8e5))
* **ci:** sort preview tags by version and filter server-only tags ([#202](https://github.com/stardew-valley-dedicated-server/server/issues/202)) ([f0e43e8](https://github.com/stardew-valley-dedicated-server/server/commit/f0e43e8b47b4cec6b64662c82d166aa45f059568))
* **ci:** use prepare job for dynamic matrix filtering ([#114](https://github.com/stardew-valley-dedicated-server/server/issues/114)) ([3e91209](https://github.com/stardew-valley-dedicated-server/server/commit/3e9120919d94649438668cd67f7ab4f33b3d2d49))
* **ci:** use release-please pr for preview version calculation ([7df78c1](https://github.com/stardew-valley-dedicated-server/server/commit/7df78c18a63e3bb93ec89911fc9f90539cdd1a70))
* **discord-bot:** use supported activity type for offline detection ([#173](https://github.com/stardew-valley-dedicated-server/server/issues/173)) ([193fb05](https://github.com/stardew-valley-dedicated-server/server/commit/193fb05b50860a700c372f011d8c85ea6da8ce08))
* **docker:** remove redundant tail process causing duplicate logs ([#125](https://github.com/stardew-valley-dedicated-server/server/issues/125)) ([b9ce425](https://github.com/stardew-valley-dedicated-server/server/commit/b9ce425848351ffd94782f24a1b7e39d2c45245e))
* **invite-code:** add logging and improve error handling for InviteCo… ([#123](https://github.com/stardew-valley-dedicated-server/server/issues/123)) ([193d240](https://github.com/stardew-valley-dedicated-server/server/commit/193d240fd0d0742dec59c9cd62822d08fd8a7ec6))
* **mods:** fix space core crash due to missing debug symbols ([#190](https://github.com/stardew-valley-dedicated-server/server/issues/190)) ([7f19a8b](https://github.com/stardew-valley-dedicated-server/server/commit/7f19a8b10f491359911c4b89413f99147bd506ef))
* release preparation fixes ([#171](https://github.com/stardew-valley-dedicated-server/server/issues/171)) ([db41582](https://github.com/stardew-valley-dedicated-server/server/commit/db41582a72bb46f9cd58a822b8402783efa1bc9e))
* set correct Steam AppID for SDR connections ([#170](https://github.com/stardew-valley-dedicated-server/server/issues/170)) ([6fc46df](https://github.com/stardew-valley-dedicated-server/server/commit/6fc46df4cec4ed486e5f2524282d6af92ddc73b7))
* **steam-service:** add checksum validation for downloaded game files ([#174](https://github.com/stardew-valley-dedicated-server/server/issues/174)) ([0eb63b8](https://github.com/stardew-valley-dedicated-server/server/commit/0eb63b80a9ae060fb7ebaa3a9155c167a038a166))
* **tests:** repair broken tests after multiple merges ([#179](https://github.com/stardew-valley-dedicated-server/server/issues/179)) ([b33f5eb](https://github.com/stardew-valley-dedicated-server/server/commit/b33f5eb4396cd471b2bbfdccbda10f09312b9a93))


### Documentation

* add direct github links for config files in readme ([49fdf26](https://github.com/stardew-valley-dedicated-server/server/commit/49fdf2698cb8eb6796dfd4e2ec6bc9132a9775c7))
* add networking guide with GOG Galaxy vs Direct IP explanation ([56f1081](https://github.com/stardew-valley-dedicated-server/server/commit/56f10813508b4b730c81cc72f6e4bafe9f47f814))
* add Next Steps section to auth guide ([59be46f](https://github.com/stardew-valley-dedicated-server/server/commit/59be46ffbc86cd087b0c49a64a66bf0faf1fca75))
* add prerequisites guide for Docker verification ([34bb540](https://github.com/stardew-valley-dedicated-server/server/commit/34bb54055b12d15617035a8c489319d6930f14f1))
* add Steam SDR ports to docker-compose and networking docs ([#169](https://github.com/stardew-valley-dedicated-server/server/issues/169)) ([e5bae0e](https://github.com/stardew-valley-dedicated-server/server/commit/e5bae0e8c7fab004bb36225af14c2357fc3466cd))
* add steam_auth_port to env example ([eddc2db](https://github.com/stardew-valley-dedicated-server/server/commit/eddc2dbd365bcae6b206940aa64246fe43b425bd))
* add variable details section to configuration guide ([6449215](https://github.com/stardew-valley-dedicated-server/server/commit/644921540b4e29efa3151c530b570f7076aed72c))
* add volume name prefixing explanation ([1f99a7c](https://github.com/stardew-valley-dedicated-server/server/commit/1f99a7c87aa365dee758b3324116439f6db6e0a2))
* document container names in faq ([fcb8a9a](https://github.com/stardew-valley-dedicated-server/server/commit/fcb8a9a5618c397a8a2de3ea5c73833fc384ae8f))
* fix edit link path and add prerequisites/networking to sidebar ([c2018b4](https://github.com/stardew-valley-dedicated-server/server/commit/c2018b44deec66ba870a840b365c7a5e015ceeae))
* **networking:** document Steam SDR architecture ([630df42](https://github.com/stardew-valley-dedicated-server/server/commit/630df427c1e00040b50cb1e83444775be173ae30))
* restructure and improve documentation ([#196](https://github.com/stardew-valley-dedicated-server/server/issues/196)) ([5c25416](https://github.com/stardew-valley-dedicated-server/server/commit/5c25416b127ed1485c015e1926ec0ff95bc28c65))
* standardize docker command flags in auth docs ([7c34d1b](https://github.com/stardew-valley-dedicated-server/server/commit/7c34d1b86f295bc5b0047aadb19e24c82cd7fb4b))

## [1.4.1](https://github.com/stardew-valley-dedicated-server/server/compare/sdvd-server-v1.4.0...sdvd-server-v1.4.1) (2026-01-17)


### Bug Fixes

* **ci:** remove duplicate release-please from steam-service workflow ([#96](https://github.com/stardew-valley-dedicated-server/server/issues/96)) ([0821d63](https://github.com/stardew-valley-dedicated-server/server/commit/0821d63091579fc4ef898728503ac72a3beb29ba))

## [1.4.0](https://github.com/stardew-valley-dedicated-server/server/compare/sdvd-server-v1.3.0...sdvd-server-v1.4.0) (2026-01-17)


### Features

* **ci:** add discord notifications for image pushes ([#93](https://github.com/stardew-valley-dedicated-server/server/issues/93)) ([f3e7286](https://github.com/stardew-valley-dedicated-server/server/commit/f3e72866c282f3b35a2b4642d2eac5049758d630))


### Bug Fixes

* **docs:** add base path for github pages deployment ([#95](https://github.com/stardew-valley-dedicated-server/server/issues/95)) ([0f7675e](https://github.com/stardew-valley-dedicated-server/server/commit/0f7675e4512ebd9049495747a333b060ee5e458e))

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
