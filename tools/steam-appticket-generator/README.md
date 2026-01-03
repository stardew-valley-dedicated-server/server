# node-app-ticket

Simple proof of concept for getting an encrypted Steam app ticket using node-steam-user.

## Setup

```bash
npm install
```

## Usage

Run with command line arguments:

```bash
node index.js <username> <password>
```

Or use environment variables:

```bash
STEAM_USERNAME=your_username STEAM_PASSWORD=your_password node index.js
```

The script will:
1. Login to Steam
2. Request an encrypted app ticket for app ID 413150 (Stardew Valley)
3. Display the ticket in both hex and base64 format
4. Logout

## Note

If you have Steam Guard enabled, you may need to handle the authentication code. This basic POC doesn't include Steam Guard support yet.
