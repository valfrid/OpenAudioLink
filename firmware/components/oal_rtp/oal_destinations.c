#include "oal_destinations.h"

#include <string.h>

bool oal_address_is_ipv4(const char *address)
{
    if (address == NULL) {
        return false;
    }

    size_t i = 0;
    for (int octet = 0; octet < 4; octet++) {
        if (octet > 0) {
            if (address[i] != '.') {
                return false;
            }
            i++;
        }

        if (address[i] < '0' || address[i] > '9') {
            return false;
        }
        /* "010" is octal to inet_addr, which silently makes it a different
         * host from the one that was typed. */
        if (address[i] == '0' && address[i + 1] >= '0' && address[i + 1] <= '9') {
            return false;
        }

        int value = 0;
        int digits = 0;
        while (address[i] >= '0' && address[i] <= '9') {
            value = value * 10 + (address[i] - '0');
            digits++;
            i++;
            if (digits > 3 || value > 255) {
                return false;
            }
        }
    }

    return address[i] == '\0';
}

void oal_destinations_reset(oal_destinations_t *set)
{
    if (set != NULL) {
        memset(set, 0, sizeof(*set));
    }
}

bool oal_destinations_contains(const oal_destinations_t *set, const char *address)
{
    if (set == NULL || address == NULL) {
        return false;
    }
    for (size_t i = 0; i < set->count; i++) {
        if (strcmp(set->entries[i], address) == 0) {
            return true;
        }
    }
    return false;
}

oal_destinations_result_t oal_destinations_add(oal_destinations_t *set, const char *address)
{
    if (set == NULL || !oal_address_is_ipv4(address)) {
        return OAL_DEST_INVALID;
    }
    if (oal_destinations_contains(set, address)) {
        return OAL_DEST_ALREADY_PRESENT;
    }
    if (set->count >= OAL_STREAM_MAX_DESTINATIONS) {
        return OAL_DEST_FULL;
    }

    /* Length is bounded by oal_address_is_ipv4 having accepted it: fifteen
     * characters at most, and the array holds sixteen. */
    strcpy(set->entries[set->count], address);
    set->count++;
    return OAL_DEST_ADDED;
}

bool oal_destinations_remove(oal_destinations_t *set, const char *address)
{
    if (set == NULL || address == NULL) {
        return false;
    }

    for (size_t i = 0; i < set->count; i++) {
        if (strcmp(set->entries[i], address) != 0) {
            continue;
        }
        /* Move the last entry into the hole rather than shifting the tail.
         * Order carries no meaning here — every destination receives the
         * same packet — so the cheaper operation is also the correct one. */
        set->count--;
        if (i != set->count) {
            memcpy(set->entries[i], set->entries[set->count], OAL_ADDRESS_MAX);
        }
        memset(set->entries[set->count], 0, OAL_ADDRESS_MAX);
        return true;
    }
    return false;
}
