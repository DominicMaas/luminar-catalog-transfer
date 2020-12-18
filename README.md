# Luminar Catalog Transfer
Transfer edits from one luminar catalog to another

## Read Steps

1. Read source catalog
2. Select * on `source.img_history_states`
3. group on `source.image_history_state_proxy` (`image_history_state_proxy._state_id_int_64` <--> `img_history_states._id_int_64`
4. Match on `images._id_int_64` (`images.` <--> `image_history_state_proxy._image_id_int_64`)

## Import Steps

1. Read destination catalog
2. Find matching source catalog images with destionation images (using `images.path_wide_ch`)
3. Insert `img_history_states` (keeping track of new inserted ids)
4. Write proxy matchup within `image_history_state_proxy` between new image id and new history state id

## Unkowns

There is a `resources` match (`img_history_state_resources` and `resources`), unsure if this is important. there are hardcoded links to the cache folder that I cannot use anyway (Win10 --> macOS)
