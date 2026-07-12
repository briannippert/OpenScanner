SELECT id, kind, value, name, alphaTag, frequency
FROM aliases
ORDER BY alphaTag, frequency, kind, value
