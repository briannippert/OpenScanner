UPDATE aliases
SET kind = @Kind, value = @Value, name = @Name, alphaTag = @AlphaTag, frequency = @Frequency
WHERE id = @Id
