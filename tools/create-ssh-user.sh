#!/bin/bash
set -euo pipefail

# Creates a dedicated SSH user for GitHub Actions deployments.
# Deployments go to ~/srv/<environment> (e.g., ~/srv/public-test)

USERNAME="github_deploy"
USER_HOME="/home/$USERNAME"
USER_SSH_DIR="$USER_HOME/.ssh"
USER_SSH_KEY_PATH="$USER_SSH_DIR/$USERNAME"
USER_SRV_DIR="$USER_HOME/srv"

# Create user (if not exists)
if ! id "$USERNAME" >/dev/null 2>&1; then
    useradd -m -s /bin/bash "$USERNAME"
    passwd -l "$USERNAME"
    echo "Created user: $USERNAME"
fi

# Add to docker group
usermod -aG docker "$USERNAME"

# Set up home directory
chmod 700 "$USER_HOME"

# Set up SSH directory and keys
mkdir -p "$USER_SSH_DIR"
chmod 700 "$USER_SSH_DIR"
chown "$USERNAME:$USERNAME" "$USER_SSH_DIR"

if [ ! -f "$USER_SSH_KEY_PATH" ]; then
    runuser -l "$USERNAME" -c "ssh-keygen -t ed25519 -C '$USERNAME' -f '$USER_SSH_KEY_PATH' -N ''"
    echo "Generated SSH key: $USER_SSH_KEY_PATH"
fi
chmod 600 "$USER_SSH_KEY_PATH"

AUTH_KEYS="$USER_SSH_DIR/authorized_keys"
touch "$AUTH_KEYS"
grep -qxF "$(cat "$USER_SSH_KEY_PATH.pub")" "$AUTH_KEYS" || cat "$USER_SSH_KEY_PATH.pub" >> "$AUTH_KEYS"
chmod 600 "$AUTH_KEYS"
chown "$USERNAME:$USERNAME" "$AUTH_KEYS"

# Set up deployment directory (~/srv)
mkdir -p "$USER_SRV_DIR"
chown "$USERNAME:$USERNAME" "$USER_SRV_DIR"
chmod 755 "$USER_SRV_DIR"

echo ""
echo "Setup complete!"
echo ""
echo "Add these secrets to your GitHub Environment:"
echo "  DEPLOY_SSH_USER: $USERNAME"
echo "  DEPLOY_SSH_KEY:"
echo ""
cat "$USER_SSH_KEY_PATH"
echo ""
