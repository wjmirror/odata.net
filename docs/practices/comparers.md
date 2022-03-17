This week's topic is about the standard implementations for the different ways to compare instances of a type. There are 7 interfaces in .NET that are used for this: IEqualityComparer, IEqualityComparer<T>, IComparer, IComparer<T>, Comparable, IComparable<T>, and IEquatable<T>. Notice the missing, non-generic IEquatable variant. This is because object already has those methods that can be overridden by any derived type. Let's know look at the standard implementations:
  
```
public sealed class FooComparer : IEqualityComparer
{
  public bool Equals(object x, object y)
  {
    if (object.ReferenceEquals(x, y))
    {
      return true;
    }

    if (x == null || y == null)
    {
      return false;
    }
  
    // implementation specific logic should go here
    ...
  }
  
  public int GetHashCode(object obj)
  {
    if (obj == null)
    {
      throw new ArgumentNullException(nameof(obj));
    }
  
    // implementation specific logic should go here
    ...
  }
}
```

There are a few notes to observe about this implementation. First, the use of `object.ReferenceEquals`. Normally, we could just write `if (x == y)`. However, because `x` is of type `object`, we do not know if `object.Equals` has been overloaded for the underlying type of `x`. This might mean that `x == y` will perform many more operations than are necessary if `x` and `y` are the same instance. `object.ReferenceEquals` performs *exactly* this check. Second, note that we do not need to be concerned about `null` once the first two `if` statements are evaluated. If `x` and `y` are both `null`, `object.ReferenceEquals` will cause use to return `true`. This means that the second `if` statement does not need to check if they are both `null`, only if either one of them is `null`. If either is `null`, we know they must be different because we already know they aren't *both* `null`. Third, there is a possible exception that can be thrown in `GetHashCode`. Although it can be tempting to use a default value for the case where `obj` is `null` (often `0` is used), it is best to follow the contract specified by the interface and let the caller dictate whether to handle the `null` case or not. Using a default value ultimately skews the distribution of hash codes, which is not always ideal. 
