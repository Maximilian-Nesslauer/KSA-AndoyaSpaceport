# Changelog

## 1.0.1

Correct texture coordinates on two hidden surfaces, validated against KSA build version 2026.11.10.5801. The ground skirt under the site had no texture area, and the faces in the corner of the rock armour where the causeway leaves the quay had their texture coordinates on a line. The game builds the surface shading of a face from its texture coordinates, so these faces had no valid shading data. The skirt now has a plain texture area of its own, and the armour corner takes the rock texture at its true width. Both surfaces are covered by the terrain or by the rock slopes, so the site looks the same as before. Only the meshes and the textures of these faces changed: the layout of the site, its colliders, the code part and saved games are the same as in 1.0.0.

## 1.0.0

First release, validated against KSA build version 2026.11.10.5801: the launch site Andoya Spaceport on Earth with one pad, tower, tank farm and hall, lamps at night, sun shadows, and walkable tower stairs and flame trench.
