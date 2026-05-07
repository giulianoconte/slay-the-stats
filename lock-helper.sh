# Sourced by deploy.sh and redeploy.sh. Coordinates the cross-tree lock at
# /tmp/sts2-redeploy.lock so only one watcher is active at a time across all
# sts2 mod source trees.

LOCK_FILE=/tmp/sts2-redeploy.lock

# acquire_lock <mode> <force>
#   mode  = "deploy" | "redeploy" (informational only; written into the lock)
#   force = "force" to override an active watcher in another tree, or "" otherwise
# Returns 0 if the lock is now ours to write; 1 if held by a foreign tree and
# --force was not passed.
acquire_lock() {
    local mode=$1
    local force=$2
    local tree=$SCRIPT_DIR

    if [[ -f "$LOCK_FILE" ]]; then
        local other_pid other_tree other_mode other_start
        other_pid=$(awk -F= '/^pid=/{print $2; exit}' "$LOCK_FILE")
        other_tree=$(awk -F= '/^tree=/{print $2; exit}' "$LOCK_FILE")
        other_mode=$(awk -F= '/^mode=/{print $2; exit}' "$LOCK_FILE")
        other_start=$(awk -F= '/^start=/{print $2; exit}' "$LOCK_FILE")

        if [[ -n "$other_pid" ]] && kill -0 "$other_pid" 2>/dev/null; then
            local age=$(( $(date +%s) - ${other_start:-0} ))
            if [[ "$other_tree" == "$tree" ]]; then
                echo "Replacing prior watcher for this tree (pid $other_pid, age ${age}s)."
                kill "$other_pid" 2>/dev/null || true
                local i
                for i in 1 2 3 4 5; do
                    kill -0 "$other_pid" 2>/dev/null || break
                    sleep 0.2
                done
            elif [[ "$force" == "force" ]]; then
                echo "Forcing kill of watcher in $other_tree (pid $other_pid, age ${age}s)."
                kill "$other_pid" 2>/dev/null || true
                sleep 0.5
            else
                echo "ERROR: another redeploy's watcher is active:" >&2
                echo "  tree:  $other_tree" >&2
                echo "  pid:   $other_pid (age ${age}s, mode=$other_mode)" >&2
                echo "  log:   $other_tree/logs/.watch.log" >&2
                echo "Close the game to release, or pass --force to take over." >&2
                return 1
            fi
        fi
    fi
    return 0
}

# write_lock <pid> <mode>
write_lock() {
    cat > "$LOCK_FILE" <<EOF
tree=$SCRIPT_DIR
pid=$1
mode=$2
start=$(date +%s)
EOF
}
