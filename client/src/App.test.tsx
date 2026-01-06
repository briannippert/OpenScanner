import { render } from '@testing-library/react';
import { describe, it, expect } from 'vitest';
import VuMeter from './components/VuMeter';

describe('Environment Setup', () => {
  it('should pass a basic truthy test', () => {
    expect(true).toBe(true);
  });
});

describe('VuMeter Component', () => {
  it('renders without crashing', () => {
    const { container } = render(<VuMeter />);
    const canvas = container.querySelector('canvas');
    expect(canvas).toBeInTheDocument();
  });

  it('renders with custom dimensions', () => {
    const { container } = render(<VuMeter width={50} height={100} />);
    const canvas = container.querySelector('canvas');
    expect(canvas).toHaveAttribute('width', '50');
    expect(canvas).toHaveAttribute('height', '100');
  });
});
