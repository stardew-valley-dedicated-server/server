# Architecture

> [!WARNING]
> This section needs work.

## Overview
|Repo|Description|
|---|---|
|[Server](https://github.com/stardew-valley-dedicated-server/server)|This repository|
|[Web UI](https://github.com/stardew-valley-dedicated-server/web)|Web based admin interface based on Nuxt3 (**not fully released yet**)|
|[AsyncAPI TS](https://github.com/stardew-valley-dedicated-server/asyncapi-generator-template-ts)|AsyncAPI template to generate a strongly typed TS websocket client|
|[AsyncAPI C#](https://github.com/stardew-valley-dedicated-server/asyncapi-generator-template-cs)|AsyncAPI template to generate a strongly typed C# websocket client|


## Steam Client
Running the client alongside the game would allow us to enable achievements and invite codes.

However, we currently do not include it for several reasons:
* UI interaction is hard to automate (focus needs mouse over steam window, hidden windows with duplicated titles etc.)
* Has known bugs not exclusive to this project (close button not working, non-deterministic behaviour in general)
* Doubles or triples the image build time, image size and startup time

## Build Process
This project uses a quite complex build process to provide a smooth and flexible development experience.

### Develop Build
TODO: Description here

### Production Build
TODO: Description here
