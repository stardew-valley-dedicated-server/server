mkfile_path := $(abspath $(lastword $(MAKEFILE_LIST)))
current_dir := $(dir $(mkfile_path))

generate_protos:
	buf generate https://github.com/JunimoHost/junimo-api.git --path junimohost/stardewsteamauth --path junimohost/stardewgame