FROM python:3.12-slim

RUN pip install --no-cache-dir discord-cli-agent==0.12.0

# Run as a non-root user rather than root. discli stores its state under $HOME/.discli, so point
# HOME at a dedicated dir. The container's user comes from docker-compose `user:` (any USER_ID),
# and there is no root phase to chown for it, so the dir is world-writable: a fresh Docker volume
# mounted there (see docker-compose.yml) inherits these bits and any uid can write its state.
ENV HOME=/home/discli
RUN mkdir -p /home/discli/.discli && \
    chown -R 1000:1000 /home/discli && \
    chmod 0777 /home/discli /home/discli/.discli
