# Make References Cleanup

We currently have hardcoded references to run "make" commands, even for the remotely pushed docker image where make is not available or intended to be used. Users who just pull the image and not build it locally do not use make, they use normal docker/docker compose commands.

See the "make setup" in one of our startup banners:

```
Game files not found! Please run setup first:
make setup
```

## Objective

We need to log hints that work for people who only run the image after pulling it, e.g. instead of "make setup" we must show "docker compose run --rm -it steam-auth setup" in the log
