-- Keyspace is selected by CassandraDatabase. These tables are shared by API and labs.
CREATE TABLE IF NOT EXISTS reservations_by_confirmation (
    confirmation_number text PRIMARY KEY,
    hotel_id text,
    start_date date,
    end_date date,
    room_number smallint,
    guest_id uuid
);

-- One partition per hotel and arrival date. Confirmation number prevents overwrites.
CREATE TABLE IF NOT EXISTS reservations_by_hotel_date (
    hotel_id text,
    start_date date,
    room_number smallint,
    confirmation_number text,
    end_date date,
    guest_id uuid,
    PRIMARY KEY ((hotel_id, start_date), room_number, confirmation_number)
) WITH CLUSTERING ORDER BY (room_number ASC, confirmation_number ASC);
