# ~/.profile: executed by sh for login shells

# Display welcome message
cat << 'EOF'
Connected to the docker container shell.

Exit and run 'make cli' if you want to use the server CLI.
EOF

# If running bash, source .bashrc if it exists
if [ -n "$BASH_VERSION" ]; then
    if [ -f "$HOME/.bashrc" ]; then
        . "$HOME/.bashrc"
    fi
fi
