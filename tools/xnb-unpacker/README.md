# XNB Unpacker

Unpacks Stardew Valley `Content` folder using [Pathoschild's StardewXnbHack](https://github.com/Pathoschild/StardewXnbHack/releases) inside docker.

Build the image:

```shell
docker build -t xnb-unpacker .
```

Then run the container:

```shell
docker run --rm -it \
    -v "$GAME_PATH:/game" \
    -v "../../decompiled/content:/game/Content (unpacked)" \
    xnb-unpacker
```

> Bind mount to `/game/Content (unpacked)` is optional, but useful to be able to string-search through the unpacked files inside your IDE.
