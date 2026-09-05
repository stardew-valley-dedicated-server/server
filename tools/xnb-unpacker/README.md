# XNB Unpacker

Unpacks Stardew Valley `Content` folder using [Pathoschild's StardewXnbHack](https://github.com/Pathoschild/StardewXnbHack/releases) inside docker.

Build the image:

```shell
docker build -t xnb-unpacker .
```

Run it as your own user so the unpacked files belong to you:

```shell
docker run --rm -it \
    --user "$(id -u):$(id -g)" \
    -v "$GAME_PATH:/game" \
    -v "../../decompiled/content:/game/Content (unpacked)" \
    xnb-unpacker
```

> Bind mount to `/game/Content (unpacked)` is optional, but useful to be able to string-search through the unpacked files inside your IDE.
>
> On Windows and macOS `--user` is not needed; Docker Desktop handles file ownership.
