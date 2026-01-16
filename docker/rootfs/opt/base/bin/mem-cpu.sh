#!/bin/sh

get_cpu_usage() {
    read cpu user nice system idle iowait irq softirq steal guest < /proc/stat
    total1=$((user + nice + system + idle + iowait + irq + softirq + steal))
    idle1=$((idle + iowait))

    sleep 0.5

    read cpu user nice system idle iowait irq softirq steal guest < /proc/stat
    total2=$((user + nice + system + idle + iowait + irq + softirq + steal))
    idle2=$((idle + iowait))

    total_diff=$((total2 - total1))
    idle_diff=$((idle2 - idle1))

    cpu_usage=$(( (100 * (total_diff - idle_diff) / total_diff) ))
    echo "$cpu_usage"
}

get_disk_io_load() {
    # Sum reads/writes of all disks (sectors)
    read r1 w1 < <(awk '{r+=$6; w+=$10} END{print r, w}' /proc/diskstats)

    sleep 0.5

    read r2 w2 < <(awk '{r+=$6; w+=$10} END{print r, w}' /proc/diskstats)

    # Delta in KB
    dr_kb=$(( (r2 - r1) / 2 ))
    dw_kb=$(( (w2 - w1) / 2 ))

    # Convert KB/s to MB/s with 1 decimal
    dr_mb=$(awk "BEGIN {printf \"%.1f\", $dr_kb/1024}")
    dw_mb=$(awk "BEGIN {printf \"%.1f\", $dw_kb/1024}")

    # Color arrows if non-zero
    GREEN="#[fg=colour46]"  # green for reads
    RED="#[fg=colour196]"   # red for writes
    RESET="#[default]"

    # Color arrows if non-zero (based on integer KB values)
    if [ "$dr_kb" -gt 0 ]; then
        read_arrow="${GREEN}↗${RESET}"
    else
        read_arrow="↗"
    fi

    if [ "$dw_kb" -gt 0 ]; then
        write_arrow="${RED}↘${RESET}"
    else
        write_arrow="↘"
    fi

    echo "${dr_mb}MB/s $read_arrow ${dw_mb}MB/s $write_arrow"
}


# Collect metrics
cpu=$(get_cpu_usage)
load=$(cut -d " " -f1-3 /proc/loadavg)
io=$(get_disk_io_load)

# Memory usage in GB
read mem_used_mb mem_total_mb < <(free -m | awk '/Mem:/ {print $3, $2}')

# Convert MB to GB with one decimal
mem_used=$(awk "BEGIN {printf \"%.1f\", $mem_used_mb/1024}")
mem_total=$(awk "BEGIN {printf \"%.1f\", $mem_total_mb/1024}")

mem="${mem_used}/${mem_total}GB"
mem_pct=$(( 100 * mem_used_mb / mem_total_mb ))

# Tmux color vars
LABEL="#[fg=colour195]"
VALUE="#[fg=#ffffff]"
RESET="#[default]"

# CPU gradient
if [ "$cpu" -ge 80 ]; then
    CPU_COLOR="#[fg=colour196]"  # red
elif [ "$cpu" -ge 50 ]; then
    CPU_COLOR="#[fg=colour226]"  # yellow
else
    CPU_COLOR="$VALUE"           # default
fi

# Memory gradient (similar to CPU)
if [ "$mem_pct" -ge 80 ]; then
    MEM_COLOR="#[fg=colour196]"  # red
elif [ "$mem_pct" -ge 50 ]; then
    MEM_COLOR="#[fg=colour226]"  # yellow
else
    MEM_COLOR="$VALUE"           # default
fi

echo "${MEM_COLOR}$mem${RESET} | ${CPU_COLOR}$cpu%${RESET} | ${VALUE}$load${RESET} | ${VALUE}$io${RESET}"
