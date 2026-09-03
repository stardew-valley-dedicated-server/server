FROM python:3.12-slim

RUN pip install --no-cache-dir discord-cli-agent==0.12.0

# Run as a non-root user rather than root. discli stores its state under $HOME/.discli, so point
# HOME at a dedicated dir and pre-create/own it as the default uid (1000). A fresh Docker volume
# mounted there (see docker-compose.yml) inherits this ownership, so the non-root user can write
# it. The container's user is set via docker-compose `user:` (configurable through USER_ID).
ENV HOME=/home/discli
RUN mkdir -p /home/discli/.discli && \
    chown -R 1000:1000 /home/discli
