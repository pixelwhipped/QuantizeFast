# Color Quantization
Color quantization is the process of reducing number of colors used in an image while trying to maintain the visual appearance of the original image.

# The Algorithm
It's pretty simple and made so intentionally.  What we do is find the average color(center) and the for each pixel use its color as in index, distance from the center and record the pixel index.
Then for ever proceeding pixel we check if the color exists and either add the pixel index to the list(indicies) or create an new entry for that color.
Then we sort by distance from the center.
Iterate back to front until the desired color count is met be finding the next closest color and blending the two using the count of indices as a factor for the blend amount(lerp).
remove the nearest color and continue iterating. 
I dont update the distance from center as repeating the loop though all indexes should not happen too often, and because earlier colors in the list are the usually nearest that get removed the order should stay relitivly close.
Close enough is good enough.
