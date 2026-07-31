#include "oal_peers.h"

#include <string.h>

void oal_peer_table_reset(oal_peer_table_t *table)
{
    if (table != NULL) {
        memset(table, 0, sizeof(*table));
    }
}

static size_t oldest_slot(const oal_peer_table_t *table)
{
    size_t oldest = 0;
    for (size_t i = 1; i < table->count; i++) {
        if (table->entries[i].last_seen_us < table->entries[oldest].last_seen_us) {
            oldest = i;
        }
    }
    return oldest;
}

bool oal_peer_table_record(oal_peer_table_t *table, const oal_peer_t *peer)
{
    if (table == NULL || peer == NULL || peer->id[0] == '\0') {
        return false;
    }

    for (size_t i = 0; i < table->count; i++) {
        if (strcmp(table->entries[i].id, peer->id) == 0) {
            table->entries[i] = *peer;
            return false;
        }
    }

    if (table->count < OAL_MAX_PEERS) {
        table->entries[table->count++] = *peer;
        return true;
    }

    table->entries[oldest_slot(table)] = *peer;
    return true;
}

size_t oal_peer_table_live(const oal_peer_table_t *table, int64_t now_us,
                           oal_peer_t *out, size_t max)
{
    if (table == NULL || out == NULL || max == 0) {
        return 0;
    }

    const int64_t cutoff = now_us - (int64_t)OAL_PEER_STALE_MS * 1000;
    size_t written = 0;
    for (size_t i = 0; i < table->count && written < max; i++) {
        if (table->entries[i].last_seen_us >= cutoff) {
            out[written++] = table->entries[i];
        }
    }

    /* Newest first. Insertion sort over at most sixteen entries costs less
     * than the qsort call it would take to look tidier. */
    for (size_t i = 1; i < written; i++) {
        oal_peer_t key = out[i];
        size_t j = i;
        while (j > 0 && out[j - 1].last_seen_us < key.last_seen_us) {
            out[j] = out[j - 1];
            j--;
        }
        out[j] = key;
    }
    return written;
}

size_t oal_peer_table_count_live(const oal_peer_table_t *table, int64_t now_us)
{
    if (table == NULL) {
        return 0;
    }

    const int64_t cutoff = now_us - (int64_t)OAL_PEER_STALE_MS * 1000;
    size_t live = 0;
    for (size_t i = 0; i < table->count; i++) {
        if (table->entries[i].last_seen_us >= cutoff) {
            live++;
        }
    }
    return live;
}
